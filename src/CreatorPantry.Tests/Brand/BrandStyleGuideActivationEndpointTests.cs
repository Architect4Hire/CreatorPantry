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
/// <c>POST .../brand-style-guides/{guideId}/versions/{versionNumber}/activation</c> through the real Gateway:
/// the activation, the replacement, the replay, the expectation, every refusal, and that neither workspace can
/// see or move the other's default.
/// </summary>
/// <remarks>
/// <para>
/// Approvals are seeded directly, because nothing in the API writes one yet: the approval route is a later
/// prompt, and activation is defined only over an already-approved version. Later versions are seeded for the
/// same reason (11A.15 edits). Replacements go through the real route, because staleness is a fact about what
/// a replacement leaves behind.
/// </para>
/// <para>
/// <strong>The save-race conflict is not exercised here.</strong> It depends on SQL Server bumping
/// <c>RowVersion</c> on update, which SQLite cannot do — <see cref="SqliteModelCustomizer"/> gives the column a
/// value on insert and nothing changes it after. <c>BrandGuideSqlServerTests</c> covers that mechanism against
/// a real server; what this file covers is the expectation check, which is where a creator meets the same
/// refusal.
/// </para>
/// </remarks>
public sealed class BrandStyleGuideActivationEndpointTests : IAsyncLifetime
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

    // ---- Activating ----

    [Fact]
    public async Task An_approved_version_becomes_the_workspace_default()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await ApprovedGuideAsync(client, _fixture.WorkspaceA);

        var response = await ActivateAsync(client, _fixture.WorkspaceA, guide.Id, 1, new
        {
            confirmed = true,
            reason = "Launch voice",
        });

        // 200 rather than 201: the workspace default is a singleton being repointed, not a new subresource.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains(IdempotencyPolicy.ReplayedHeader));

        var body = await BodyOf(response);
        Assert.Equal(guide.Id, body.GetProperty("guideId").GetGuid());
        Assert.Equal(guide.VersionId, body.GetProperty("versionId").GetGuid());
        Assert.Equal(1, body.GetProperty("versionNumber").GetInt32());
        Assert.Equal("Launch voice", body.GetProperty("reason").GetString());
        Assert.False(body.GetProperty("alreadyActive").GetBoolean());
        Assert.NotEqual(Guid.Empty, body.GetProperty("activatedByMembershipId").GetGuid());

        // Nothing was replaced, because the workspace had no default.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("replaced").ValueKind);

        var stored = await DefaultAsync(_fixture.WorkspaceA);
        Assert.NotNull(stored);
        Assert.Equal(guide.VersionId, stored.BrandStyleGuideVersionId);
        Assert.Equal("Launch voice", stored.Reason);
        Assert.Equal(body.GetProperty("activatedAt").GetDateTimeOffset(), stored.ActivatedAt);

        // The read route now reports it, which is how a client learns the expectation for the next activation.
        var read = await BodyOf(await client.GetAsync(
            $"{GuidesIn(_fixture.WorkspaceA)}/{guide.Id}", TestContext.Current.CancellationToken));
        Assert.Equal(guide.VersionId, read.GetProperty("activeVersion").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Activation_writes_one_audit_entry_naming_the_versions_and_no_creator_text()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await ApprovedGuideAsync(client, _fixture.WorkspaceA);

        await Activated(client, _fixture.WorkspaceA, guide.Id, 1, new { confirmed = true, reason = "A secret note" });

        var entry = Assert.Single(await AuditEntriesAsync(_fixture.WorkspaceA));
        Assert.Equal(BrandAuditActions.StyleGuideResourceType, entry.ResourceType);
        Assert.Equal(guide.Id.ToString("D"), entry.ResourceId);
        Assert.Null(entry.BeforeReference);
        Assert.Equal($"{guide.Id:N}:1", entry.AfterReference);

        // The activator's own words stay on the activation row. An audit summary has to be safe to display.
        Assert.DoesNotContain("A secret note", entry.Summary, StringComparison.Ordinal);
        Assert.Contains("version 1", entry.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Activating_a_second_version_replaces_the_first_and_leaves_one_default()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await ApprovedGuideAsync(client, _fixture.WorkspaceA);
        var second = await ApprovedVersionAsync(_fixture.WorkspaceA, guide.Id, 2, guide.VersionId);

        await Activated(client, _fixture.WorkspaceA, guide.Id, 1, new { confirmed = true });

        var body = await Activated(client, _fixture.WorkspaceA, guide.Id, 2, new
        {
            confirmed = true,
            expectedActiveVersionId = guide.VersionId,
            reason = "Revised voice",
        });

        Assert.Equal(second, body.GetProperty("versionId").GetGuid());

        var replaced = body.GetProperty("replaced");
        Assert.Equal(guide.Id, replaced.GetProperty("guideId").GetGuid());
        Assert.Equal(guide.VersionId, replaced.GetProperty("versionId").GetGuid());
        Assert.Equal(1, replaced.GetProperty("versionNumber").GetInt32());

        // Repointed, not added to: the workspace is the table's whole key.
        var stored = await DefaultAsync(_fixture.WorkspaceA);
        Assert.Equal(second, stored!.BrandStyleGuideVersionId);
        Assert.Equal("Revised voice", stored.Reason);
        Assert.Equal(1, await DefaultCountAsync(_fixture.WorkspaceA));

        var entries = await AuditEntriesAsync(_fixture.WorkspaceA);
        Assert.Equal(2, entries.Count);
        Assert.Equal($"{guide.Id:N}:1", entries[^1].BeforeReference);
        Assert.Equal($"{guide.Id:N}:2", entries[^1].AfterReference);
    }

    [Fact]
    public async Task The_default_can_move_to_another_guide_and_says_which_one_it_left()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var first = await ApprovedGuideAsync(client, _fixture.WorkspaceA, "House voice");
        var second = await ApprovedGuideAsync(client, _fixture.WorkspaceA, "Holiday voice");

        await Activated(client, _fixture.WorkspaceA, first.Id, 1, new { confirmed = true });

        var body = await Activated(client, _fixture.WorkspaceA, second.Id, 1, new
        {
            confirmed = true,
            expectedActiveVersionId = first.VersionId,
        });

        Assert.Equal(second.VersionId, body.GetProperty("versionId").GetGuid());
        Assert.Equal(first.Id, body.GetProperty("replaced").GetProperty("guideId").GetGuid());
        Assert.Equal(1, await DefaultCountAsync(_fixture.WorkspaceA));

        // The audit says which guide it left, and the before reference names it: a version number alone would
        // be ambiguous once the default can move between guides.
        var entries = await AuditEntriesAsync(_fixture.WorkspaceA);
        Assert.Equal(2, entries.Count);
        Assert.Contains("another guide", entries[^1].Summary, StringComparison.Ordinal);
        Assert.Equal($"{first.Id:N}:1", entries[^1].BeforeReference);
        Assert.Equal($"{second.Id:N}:1", entries[^1].AfterReference);
        Assert.Equal(second.Id.ToString("D"), entries[^1].ResourceId);

        // The guide that lost the default still reads, and reports no active version of its own.
        var read = await BodyOf(await client.GetAsync(
            $"{GuidesIn(_fixture.WorkspaceA)}/{first.Id}", TestContext.Current.CancellationToken));
        Assert.Equal(JsonValueKind.Null, read.GetProperty("activeVersion").ValueKind);
    }

    [Fact]
    public async Task Activating_the_version_that_already_holds_the_default_writes_nothing()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await ApprovedGuideAsync(client, _fixture.WorkspaceA);

        var first = await Activated(client, _fixture.WorkspaceA, guide.Id, 1, new
        {
            confirmed = true,
            reason = "Launch voice",
        });

        // A different idempotency key, so this is a second request rather than a replay of the first.
        var again = await Activated(client, _fixture.WorkspaceA, guide.Id, 1, new
        {
            confirmed = true,
            expectedActiveVersionId = guide.VersionId,
            reason = "Said again",
        });

        Assert.True(again.GetProperty("alreadyActive").GetBoolean());

        // The original activation, not this request's: nothing moved, so nothing about it is new.
        Assert.Equal(first.GetProperty("activatedAt").GetDateTimeOffset(), again.GetProperty("activatedAt").GetDateTimeOffset());
        Assert.Equal("Launch voice", again.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, again.GetProperty("replaced").ValueKind);

        var stored = await DefaultAsync(_fixture.WorkspaceA);
        Assert.Equal("Launch voice", stored!.Reason);

        // One activation happened, so there is one entry. A no-op in the trail would be noise.
        Assert.Single(await AuditEntriesAsync(_fixture.WorkspaceA));
    }

    // ---- Replay ----

    [Fact]
    public async Task A_replayed_key_returns_the_first_activation_and_writes_nothing_further()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await ApprovedGuideAsync(client, _fixture.WorkspaceA);
        var key = Guid.NewGuid().ToString("N");
        var body = new { confirmed = true, reason = "Launch voice" };

        var first = await ActivateAsync(client, _fixture.WorkspaceA, guide.Id, 1, body, key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var replay = await ActivateAsync(client, _fixture.WorkspaceA, guide.Id, 1, body, key);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues(IdempotencyPolicy.ReplayedHeader).Single());

        // The same response, down to the instant: a replay returns the committed result rather than deciding
        // again. Deciding again would have refused, because the expectation no longer matches.
        var original = await BodyOf(first);
        var replayed = await BodyOf(replay);
        Assert.Equal(original.GetProperty("activatedAt").GetDateTimeOffset(), replayed.GetProperty("activatedAt").GetDateTimeOffset());
        Assert.False(replayed.GetProperty("alreadyActive").GetBoolean());

        Assert.Single(await AuditEntriesAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_replay_returns_its_recorded_answer_even_after_the_default_has_moved_on()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await ApprovedGuideAsync(client, _fixture.WorkspaceA);
        var second = await ApprovedVersionAsync(_fixture.WorkspaceA, guide.Id, 2, guide.VersionId);
        var key = Guid.NewGuid().ToString("N");
        var body = new { confirmed = true, reason = "Launch voice" };

        var first = await BodyOf(await ActivateAsync(client, _fixture.WorkspaceA, guide.Id, 1, body, key));

        // Something else moves the default on, so the original request's expectation no longer holds.
        await Activated(client, _fixture.WorkspaceA, guide.Id, 2, new
        {
            confirmed = true,
            expectedActiveVersionId = guide.VersionId,
        });

        var replay = await ActivateAsync(client, _fixture.WorkspaceA, guide.Id, 1, body, key);

        // Replaying returns the committed answer rather than deciding again. Deciding again would refuse with a
        // conflict, which would tell a retrying client its first request had failed when it had not.
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues(IdempotencyPolicy.ReplayedHeader).Single());

        var replayed = await BodyOf(replay);
        Assert.Equal(guide.VersionId, replayed.GetProperty("versionId").GetGuid());
        Assert.Equal(first.GetProperty("activatedAt").GetDateTimeOffset(), replayed.GetProperty("activatedAt").GetDateTimeOffset());

        // And the replay changed nothing: version 2 still holds the default.
        Assert.Equal(second, (await DefaultAsync(_fixture.WorkspaceA))!.BrandStyleGuideVersionId);
        Assert.Equal(2, (await AuditEntriesAsync(_fixture.WorkspaceA)).Count);
    }

    [Fact]
    public async Task The_same_key_with_a_different_version_is_refused()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await ApprovedGuideAsync(client, _fixture.WorkspaceA);
        await ApprovedVersionAsync(_fixture.WorkspaceA, guide.Id, 2, guide.VersionId);
        var key = Guid.NewGuid().ToString("N");

        Assert.Equal(
            HttpStatusCode.OK,
            (await ActivateAsync(client, _fixture.WorkspaceA, guide.Id, 1, new { confirmed = true }, key)).StatusCode);

        var reused = await ActivateAsync(
            client, _fixture.WorkspaceA, guide.Id, 2, new { confirmed = true, expectedActiveVersionId = guide.VersionId }, key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, await CodeOf(reused));

        // The first activation still stands, untouched.
        Assert.Equal(guide.VersionId, (await DefaultAsync(_fixture.WorkspaceA))!.BrandStyleGuideVersionId);
    }

    [Fact]
    public async Task A_request_without_an_idempotency_key_is_refused()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await ApprovedGuideAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            ActivationOf(_fixture.WorkspaceA, guide.Id, 1),
            new { confirmed = true },
            TestContext.Current.CancellationToken);

        Assert.Equal(IdempotencyPolicy.KeyRequiredCode, await CodeOf(response));
        Assert.Null(await DefaultAsync(_fixture.WorkspaceA));
    }

    // ---- The expectation ----

    [Fact]
    public async Task An_expectation_the_workspace_has_moved_past_is_refused_and_names_what_is_active()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await ApprovedGuideAsync(client, _fixture.WorkspaceA);
        var second = await ApprovedVersionAsync(_fixture.WorkspaceA, guide.Id, 2, guide.VersionId);

        await Activated(client, _fixture.WorkspaceA, guide.Id, 2, new { confirmed = true });

        var response = await ActivateAsync(client, _fixture.WorkspaceA, guide.Id, 1, new
        {
            confirmed = true,

            // What the caller last saw: version 1 never held the default.
            expectedActiveVersionId = guide.VersionId,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = await BodyOf(response);
        Assert.Equal(BrandErrorCodes.GuideActivationConflict, problem.GetProperty("code").GetString());
        Assert.Equal(guide.Id, problem.GetProperty("activeGuideId").GetGuid());
        Assert.Equal(second, problem.GetProperty("activeVersionId").GetGuid());
        Assert.Equal(2, problem.GetProperty("activeVersionNumber").GetInt32());
        Assert.True(problem.GetProperty("errors").TryGetProperty("expectedActiveVersionId", out _));

        // Nothing moved, and the refusal left no audit entry of its own.
        Assert.Equal(second, (await DefaultAsync(_fixture.WorkspaceA))!.BrandStyleGuideVersionId);
        Assert.Single(await AuditEntriesAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task Omitting_the_expectation_while_a_default_exists_is_refused()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await ApprovedGuideAsync(client, _fixture.WorkspaceA);
        var second = await ApprovedVersionAsync(_fixture.WorkspaceA, guide.Id, 2, guide.VersionId);

        await Activated(client, _fixture.WorkspaceA, guide.Id, 1, new { confirmed = true });

        // An absent field is the claim "this workspace has no default", and it is checked rather than waived —
        // which is what stops a caller who forgot it from replacing a default they never saw.
        var response = await ActivateAsync(client, _fixture.WorkspaceA, guide.Id, 2, new { confirmed = true });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(BrandErrorCodes.GuideActivationConflict, await CodeOf(response));
        Assert.Equal(guide.VersionId, (await DefaultAsync(_fixture.WorkspaceA))!.BrandStyleGuideVersionId);
        Assert.NotEqual(second, (await DefaultAsync(_fixture.WorkspaceA))!.BrandStyleGuideVersionId);
    }

    [Fact]
    public async Task An_expectation_sent_while_the_workspace_has_no_default_is_refused_with_explicit_nulls()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await ApprovedGuideAsync(client, _fixture.WorkspaceA);

        var response = await ActivateAsync(client, _fixture.WorkspaceA, guide.Id, 1, new
        {
            confirmed = true,
            expectedActiveVersionId = Guid.NewGuid(),
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = await BodyOf(response);
        Assert.Equal(JsonValueKind.Null, problem.GetProperty("activeGuideId").ValueKind);
        Assert.Equal(JsonValueKind.Null, problem.GetProperty("activeVersionId").ValueKind);
        Assert.Equal(JsonValueKind.Null, problem.GetProperty("activeVersionNumber").ValueKind);
        Assert.Null(await DefaultAsync(_fixture.WorkspaceA));
    }

    // ---- Losing the race to write ----

    /// <remarks>
    /// Driven at the DataLayer rather than through two HTTP requests, which is what makes it deterministic:
    /// the default already exists by the time the save lands, and the activation is composing the insert that
    /// a request which read "no default" composes. The same shape as
    /// <c>BrandProfileSqlServerTests.Of_two_creates_in_one_workspace_the_second_is_refused</c>.
    /// </remarks>
    [Fact]
    public async Task An_activation_that_loses_the_race_to_write_the_first_default_is_a_conflict()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await ApprovedGuideAsync(client, _fixture.WorkspaceA);
        var second = await ApprovedVersionAsync(_fixture.WorkspaceA, guide.Id, 2, guide.VersionId);

        await Activated(client, _fixture.WorkspaceA, guide.Id, 1, new { confirmed = true, reason = "Launch voice" });

        // current: null — this request read a workspace with no default, and lost.
        var write = await WithDataLayerAsync(_fixture.WorkspaceA, (layer, _) => layer.ActivateAsync(
            current: null,
            second,
            "Mine instead",
            Guid.NewGuid(),
            Moment.AddDays(1),
            AuditOf(guide.Id),
            TestContext.Current.CancellationToken));

        // A primary-key collision, not a row-version one: reported as the same conflict all the same.
        Assert.Equal(BrandStyleGuideActivationWrite.Conflict, write);

        // The winner's activation stands whole, and the loser left nothing behind — no row, no audit entry.
        var stored = await DefaultAsync(_fixture.WorkspaceA);
        Assert.Equal(guide.VersionId, stored!.BrandStyleGuideVersionId);
        Assert.Equal("Launch voice", stored.Reason);
        Assert.Equal(1, await DefaultCountAsync(_fixture.WorkspaceA));
        Assert.Single(await AuditEntriesAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task An_activation_whose_default_was_removed_under_it_is_a_conflict()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await ApprovedGuideAsync(client, _fixture.WorkspaceA);
        var second = await ApprovedVersionAsync(_fixture.WorkspaceA, guide.Id, 2, guide.VersionId);

        // A default Business read, which is gone by the time the write runs.
        var current = new BrandStyleGuideWorkspaceDefault(
            guide.Id, guide.VersionId, 1, Moment, Guid.NewGuid(), "Launch voice");

        var write = await WithDataLayerAsync(_fixture.WorkspaceA, (layer, _) => layer.ActivateAsync(
            current,
            second,
            "Revised voice",
            Guid.NewGuid(),
            Moment.AddDays(1),
            AuditOf(guide.Id),
            TestContext.Current.CancellationToken));

        // Refused rather than quietly inserted: an insert here would record a decision against a workspace
        // state nobody has seen.
        Assert.Equal(BrandStyleGuideActivationWrite.Conflict, write);
        Assert.Null(await DefaultAsync(_fixture.WorkspaceA));
        Assert.Empty(await AuditEntriesAsync(_fixture.WorkspaceA));
    }

    /// <remarks>
    /// The schema guard behind <c>brand.guide.version.unapproved.conflict</c>, reached directly because
    /// Business refuses an unapproved version long before the write. A refusal the application did not predict
    /// must surface as a fault rather than be reported as a conflict, which would hide the bug that caused it.
    /// </remarks>
    [Fact]
    public async Task A_schema_refusal_the_application_did_not_predict_is_not_reported_as_a_conflict()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "Unapproved",
            questionnaire = new { voice = "Warm." },
        });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => WithDataLayerAsync(
            _fixture.WorkspaceA,
            (layer, _) => layer.ActivateAsync(
                current: null,
                guide.VersionId,
                null,
                Guid.NewGuid(),
                Moment,
                AuditOf(guide.Id),
                TestContext.Current.CancellationToken)));

        Assert.Null(await DefaultAsync(_fixture.WorkspaceA));
    }

    // ---- Refusals about the version named ----

    [Fact]
    public async Task A_draft_version_cannot_be_activated()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "Unapproved",
            questionnaire = new { voice = "Warm." },
        });

        var response = await ActivateAsync(client, _fixture.WorkspaceA, guide.Id, 1, new { confirmed = true });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(BrandErrorCodes.GuideVersionUnapprovedConflict, await CodeOf(response));
        Assert.Null(await DefaultAsync(_fixture.WorkspaceA));
        Assert.Empty(await AuditEntriesAsync(_fixture.WorkspaceA));
    }

    /// <remarks>
    /// The ordering Business documents: a version that is a draft is ineligible however stale the caller's
    /// picture of the workspace is, and that refusal will read the same after a re-read — so it is the one
    /// worth giving first.
    /// </remarks>
    [Fact]
    public async Task An_ineligible_version_is_reported_before_a_stale_expectation()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var approved = await ApprovedGuideAsync(client, _fixture.WorkspaceA, "Approved");
        var draft = await CreateGuideAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "Unapproved",
            questionnaire = new { voice = "Warm." },
        });

        await Activated(client, _fixture.WorkspaceA, approved.Id, 1, new { confirmed = true });

        // Both wrong at once: an unapproved target and an expectation the workspace has moved past.
        var response = await ActivateAsync(client, _fixture.WorkspaceA, draft.Id, 1, new
        {
            confirmed = true,
            expectedActiveVersionId = Guid.NewGuid(),
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(BrandErrorCodes.GuideVersionUnapprovedConflict, await CodeOf(response));
        Assert.Equal(approved.VersionId, (await DefaultAsync(_fixture.WorkspaceA))!.BrandStyleGuideVersionId);
    }

    [Fact]
    public async Task A_version_citing_a_replaced_source_cannot_be_activated()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var document = await UploadAsync(client, _fixture.WorkspaceA, "house-style.pdf");
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "From sources",
            questionnaire = new { voice = "Warm." },
            sourceDocuments = new[] { new { documentId = document.Id, versionNumber = 1 } },
        });
        await ApproveAsync(_fixture.WorkspaceA, guide.VersionId);

        // Approved and activatable until the document moves on underneath it.
        Assert.Equal(
            HttpStatusCode.OK,
            (await ActivateAsync(client, _fixture.WorkspaceA, guide.Id, 1, new { confirmed = true })).StatusCode);

        var later = await ApprovedVersionAsync(_fixture.WorkspaceA, guide.Id, 2, guide.VersionId, document.Id);
        await ReplaceAsync(client, _fixture.WorkspaceA, document);

        var response = await ActivateAsync(client, _fixture.WorkspaceA, guide.Id, 2, new
        {
            confirmed = true,
            expectedActiveVersionId = guide.VersionId,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = await BodyOf(response);
        Assert.Equal(BrandErrorCodes.GuideVersionStaleConflict, problem.GetProperty("code").GetString());
        Assert.Equal(1, problem.GetProperty("staleSourceCount").GetInt32());

        // The already-active version keeps the default: nothing deactivates it, including going stale itself.
        Assert.Equal(guide.VersionId, (await DefaultAsync(_fixture.WorkspaceA))!.BrandStyleGuideVersionId);
        Assert.NotEqual(later, (await DefaultAsync(_fixture.WorkspaceA))!.BrandStyleGuideVersionId);
    }

    [Fact]
    public async Task A_version_with_no_sections_and_no_rules_cannot_be_activated()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        // A guide may legitimately exist as a name while its creator fills it in. What it cannot be is the
        // thing every later generation is grounded on.
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, new { displayName = "Name only" });
        await ApproveAsync(_fixture.WorkspaceA, guide.VersionId);

        var response = await ActivateAsync(client, _fixture.WorkspaceA, guide.Id, 1, new { confirmed = true });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(BrandErrorCodes.GuideVersionEmptyConflict, await CodeOf(response));
        Assert.Null(await DefaultAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task An_archived_guide_cannot_hold_the_workspace_default()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await ApprovedGuideAsync(client, _fixture.WorkspaceA);
        await ArchiveGuideAsync(_fixture.WorkspaceA, guide.Id);

        var response = await ActivateAsync(client, _fixture.WorkspaceA, guide.Id, 1, new { confirmed = true });

        // 409 rather than 404: an archived guide still reads, lists and compares. It just cannot govern.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(BrandErrorCodes.GuideArchivedConflict, await CodeOf(response));
        Assert.Null(await DefaultAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_version_number_the_guide_does_not_have_is_not_found()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await ApprovedGuideAsync(client, _fixture.WorkspaceA);

        var response = await ActivateAsync(client, _fixture.WorkspaceA, guide.Id, 7, new { confirmed = true });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var problem = await BodyOf(response);
        Assert.Equal(BrandErrorCodes.GuideVersionNotFound, problem.GetProperty("code").GetString());

        // The guide is already known to be readable, so naming the route segment discloses nothing.
        Assert.True(problem.GetProperty("errors").TryGetProperty("versionNumber", out _));
    }

    // ---- The confirmation ----

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task An_unconfirmed_activation_is_refused(bool? confirmed)
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await ApprovedGuideAsync(client, _fixture.WorkspaceA);

        var response = await ActivateAsync(client, _fixture.WorkspaceA, guide.Id, 1, new { confirmed });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await BodyOf(response);
        Assert.Equal(BrandErrorCodes.GuideInvalidRequest, problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("confirmed", out _));
        Assert.Null(await DefaultAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_reason_longer_than_the_column_is_refused_before_anything_is_written()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await ApprovedGuideAsync(client, _fixture.WorkspaceA);

        var response = await ActivateAsync(client, _fixture.WorkspaceA, guide.Id, 1, new
        {
            confirmed = true,
            reason = new string('x', BrandPolicy.ReasonMaxLength + 1),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await DefaultAsync(_fixture.WorkspaceA));
    }

    // ---- Authorization ----

    [Fact]
    public async Task An_editor_cannot_change_the_workspace_default()
    {
        using var owner = await OwnerOf(_fixture.WorkspaceA);
        var guide = await ApprovedGuideAsync(owner, _fixture.WorkspaceA);

        // Workspace A's second member is an Editor: enough to create and edit a guide, not to make one govern.
        using var editor = await _fixture.SignInAsync(
            _fixture.WorkspaceA.MemberEmail, cancellationToken: TestContext.Current.CancellationToken);

        var response = await ActivateAsync(editor, _fixture.WorkspaceA, guide.Id, 1, new { confirmed = true });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await DefaultAsync(_fixture.WorkspaceA));
    }

    // ---- Isolation ----

    [Fact]
    public async Task One_workspace_cannot_activate_or_even_see_the_others_guide()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);

        var guideA = await ApprovedGuideAsync(ownerA, _fixture.WorkspaceA);
        var guideB = await ApprovedGuideAsync(ownerB, _fixture.WorkspaceB);

        // B's Owner, naming A's guide under B's own slug, and B's Owner naming a guide id that was never
        // issued at all: the same answer down to the body, which is what makes them indistinguishable.
        var crossGuide = await BodyOf(
            await ActivateAsync(ownerB, _fixture.WorkspaceB, guideA.Id, 1, new { confirmed = true }));
        var neverIssued = await BodyOf(
            await ActivateAsync(ownerB, _fixture.WorkspaceB, Guid.NewGuid(), 1, new { confirmed = true }));

        Assert.Equal(BrandErrorCodes.GuideNotFound, crossGuide.GetProperty("code").GetString());
        Assert.Equal(crossGuide.GetProperty("code").GetString(), neverIssued.GetProperty("code").GetString());
        Assert.Equal(crossGuide.GetProperty("title").GetString(), neverIssued.GetProperty("title").GetString());
        Assert.Equal(crossGuide.GetProperty("status").GetInt32(), neverIssued.GetProperty("status").GetInt32());
        Assert.Equal(
            crossGuide.GetProperty("errors").GetRawText(), neverIssued.GetProperty("errors").GetRawText());

        // B's Owner under A's slug, and under a slug nobody has: a workspace the caller is not in has to look
        // like no workspace, so 404 exactly rather than the 403 that would confirm A exists.
        var crossWorkspace = await ActivateAsync(ownerB, _fixture.WorkspaceA, guideA.Id, 1, new { confirmed = true });
        var noWorkspace = await ownerB.PostAsJsonAsync(
            $"/api/v1/workspaces/no-such-kitchen/brand-style-guides/{guideA.Id}/versions/1/activation",
            new { confirmed = true },
            Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, crossWorkspace.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noWorkspace.StatusCode);

        Assert.Null(await DefaultAsync(_fixture.WorkspaceA));
        Assert.Null(await DefaultAsync(_fixture.WorkspaceB));

        // Each workspace's own activation lands in its own workspace and nowhere else.
        await Activated(ownerA, _fixture.WorkspaceA, guideA.Id, 1, new { confirmed = true });
        Assert.Equal(guideA.VersionId, (await DefaultAsync(_fixture.WorkspaceA))!.BrandStyleGuideVersionId);
        Assert.Null(await DefaultAsync(_fixture.WorkspaceB));

        await Activated(ownerB, _fixture.WorkspaceB, guideB.Id, 1, new { confirmed = true });
        Assert.Equal(guideB.VersionId, (await DefaultAsync(_fixture.WorkspaceB))!.BrandStyleGuideVersionId);
        Assert.Equal(guideA.VersionId, (await DefaultAsync(_fixture.WorkspaceA))!.BrandStyleGuideVersionId);

        Assert.Single(await AuditEntriesAsync(_fixture.WorkspaceA));
        Assert.Single(await AuditEntriesAsync(_fixture.WorkspaceB));
    }

    [Fact]
    public async Task Another_workspaces_version_is_never_what_the_expectation_resolves_to()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);

        var guideA = await ApprovedGuideAsync(ownerA, _fixture.WorkspaceA);
        var guideB = await ApprovedGuideAsync(ownerB, _fixture.WorkspaceB);
        await Activated(ownerB, _fixture.WorkspaceB, guideB.Id, 1, new { confirmed = true });

        // A names B's active version as what it expects. B's default is not visible from A, so A still has
        // none — and the refusal says so rather than confirming that B's id resolves to anything.
        var response = await ActivateAsync(ownerA, _fixture.WorkspaceA, guideA.Id, 1, new
        {
            confirmed = true,
            expectedActiveVersionId = guideB.VersionId,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = await BodyOf(response);
        Assert.Equal(JsonValueKind.Null, problem.GetProperty("activeVersionId").ValueKind);
        Assert.Equal(JsonValueKind.Null, problem.GetProperty("activeGuideId").ValueKind);

        Assert.Null(await DefaultAsync(_fixture.WorkspaceA));
        Assert.Equal(guideB.VersionId, (await DefaultAsync(_fixture.WorkspaceB))!.BrandStyleGuideVersionId);
    }

    [Fact]
    public async Task One_workspaces_conflict_names_its_own_default_while_the_other_holds_a_different_one()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);

        var guideA = await ApprovedGuideAsync(ownerA, _fixture.WorkspaceA);
        var secondA = await ApprovedVersionAsync(_fixture.WorkspaceA, guideA.Id, 2, guideA.VersionId);
        var guideB = await ApprovedGuideAsync(ownerB, _fixture.WorkspaceB);

        // Both workspaces have a default, pointing at different versions.
        await Activated(ownerA, _fixture.WorkspaceA, guideA.Id, 1, new { confirmed = true });
        await Activated(ownerB, _fixture.WorkspaceB, guideB.Id, 1, new { confirmed = true });

        var response = await ActivateAsync(ownerA, _fixture.WorkspaceA, guideA.Id, 2, new
        {
            confirmed = true,
            expectedActiveVersionId = secondA,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // A is told about A's default and nothing of B's, even though B has one.
        var problem = await BodyOf(response);
        Assert.Equal(guideA.Id, problem.GetProperty("activeGuideId").GetGuid());
        Assert.Equal(guideA.VersionId, problem.GetProperty("activeVersionId").GetGuid());
        Assert.NotEqual(guideB.Id, problem.GetProperty("activeGuideId").GetGuid());
        Assert.NotEqual(guideB.VersionId, problem.GetProperty("activeVersionId").GetGuid());

        // Nor does A's audit trail reference anything of B's.
        var entry = Assert.Single(await AuditEntriesAsync(_fixture.WorkspaceA));
        foreach (var reference in new[] { entry.Summary, entry.BeforeReference, entry.AfterReference, entry.ResourceId })
        {
            Assert.DoesNotContain(guideB.Id.ToString("N"), reference ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(guideB.VersionId.ToString("N"), reference ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task An_idempotency_key_does_not_carry_its_recorded_response_into_another_workspace()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);

        var guideA = await ApprovedGuideAsync(ownerA, _fixture.WorkspaceA);
        var guideB = await ApprovedGuideAsync(ownerB, _fixture.WorkspaceB);
        var key = Guid.NewGuid().ToString("N");

        var inB = await BodyOf(
            await ActivateAsync(ownerB, _fixture.WorkspaceB, guideB.Id, 1, new { confirmed = true }, key));

        // The same key, the same operation, a different workspace and a different caller: a fresh request, not
        // a replay of B's.
        var inA = await ActivateAsync(ownerA, _fixture.WorkspaceA, guideA.Id, 1, new { confirmed = true }, key);

        Assert.Equal(HttpStatusCode.OK, inA.StatusCode);
        Assert.False(inA.Headers.Contains(IdempotencyPolicy.ReplayedHeader));

        var body = await BodyOf(inA);
        Assert.Equal(guideA.Id, body.GetProperty("guideId").GetGuid());
        Assert.NotEqual(inB.GetProperty("versionId").GetGuid(), body.GetProperty("versionId").GetGuid());
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

    private static string ActivationOf(SeededWorkspace workspace, Guid guideId, int versionNumber) =>
        $"{GuidesIn(workspace)}/{guideId}/versions/{versionNumber}/activation";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await BodyOf(response)).GetProperty("code").GetString();

    private async Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> ActivateAsync(
        GatewayClient client, SeededWorkspace workspace, Guid guideId, int versionNumber, object body,
        string? idempotencyKey = null) =>
        client.PostAsJsonAsync(
            ActivationOf(workspace, guideId, versionNumber),
            body,
            idempotencyKey ?? Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken);

    /// <summary>Activates and insists it worked, for the arrange half of a test about something else.</summary>
    private async Task<JsonElement> Activated(
        GatewayClient client, SeededWorkspace workspace, Guid guideId, int versionNumber, object body)
    {
        var response = await ActivateAsync(client, workspace, guideId, versionNumber, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await BodyOf(response);
    }

    private async Task<SeededGuide> CreateGuideAsync(GatewayClient client, SeededWorkspace workspace, object body)
    {
        var response = await client.PostAsJsonAsync(
            GuidesIn(workspace), body, Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await BodyOf(response);

        return new SeededGuide(
            created.GetProperty("id").GetGuid(), created.GetProperty("version").GetProperty("id").GetGuid());
    }

    /// <summary>A guide whose version 1 says something and has been approved: the activatable starting point.</summary>
    private async Task<SeededGuide> ApprovedGuideAsync(
        GatewayClient client, SeededWorkspace workspace, string displayName = "House voice")
    {
        var guide = await CreateGuideAsync(client, workspace, new
        {
            displayName,
            questionnaire = new { voice = "Warm, direct, never fussy." },
        });
        await ApproveAsync(workspace, guide.VersionId);

        return guide;
    }

    private async Task<T> InScopeAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }

    /// <summary>
    /// Runs one DataLayer call in its own request scope, for the write races an HTTP request cannot stage
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

    /// <summary>An audit entry shaped like the one Business writes, for a DataLayer call made directly.</summary>
    private static AuditEntry AuditOf(Guid guideId) => new(
        "test-user",
        BrandAuditActions.StyleGuideActivated,
        BrandAuditActions.StyleGuideResourceType,
        guideId.ToString("D"),
        Guid.NewGuid(),
        "Made a version the workspace's default brand style guide.");

    /// <summary>Approves a version, as the approval route will. Nothing in the API writes one yet.</summary>
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

    /// <summary>
    /// Adds a later version that says something and is approved, as an edit plus an approval will. Cites
    /// <paramref name="documentId"/> version 1 when one is given, which is what a replacement makes stale.
    /// </summary>
    private async Task<Guid> ApprovedVersionAsync(
        SeededWorkspace workspace, Guid guideId, int number, Guid parentId, Guid? documentId = null)
    {
        var versionId = await InScopeAsync(workspace, async db =>
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

            if (documentId is { } cited)
            {
                var sourceVersionId = await db.BrandSourceDocumentVersions
                    .Where(candidate => candidate.BrandSourceDocumentId == cited && candidate.VersionNumber == 1)
                    .Select(candidate => candidate.Id)
                    .SingleAsync(TestContext.Current.CancellationToken);

                version.SourceLinks.Add(new BrandStyleGuideSourceLink
                {
                    WorkspaceId = workspace.Id,
                    BrandStyleGuideVersionId = version.Id,
                    BrandSourceDocumentVersionId = sourceVersionId,
                });
            }

            db.BrandStyleGuideVersions.Add(version);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            return version.Id;
        });

        await ApproveAsync(workspace, versionId);

        return versionId;
    }

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

    private Task<BrandStyleGuideDefault?> DefaultAsync(SeededWorkspace workspace) =>
        InScopeAsync(workspace, db => db.BrandStyleGuideDefaults
            .AsNoTracking()
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken));

    private Task<int> DefaultCountAsync(SeededWorkspace workspace) =>
        InScopeAsync(workspace, db => db.BrandStyleGuideDefaults.CountAsync(TestContext.Current.CancellationToken));

    /// <summary>The workspace's activation entries, oldest first.</summary>
    private Task<List<AuditLog>> AuditEntriesAsync(SeededWorkspace workspace) =>
        InScopeAsync(workspace, db => db.AuditLogs
            .AsNoTracking()
            .Where(entry => entry.Action == BrandAuditActions.StyleGuideActivated)
            .OrderBy(entry => entry.OccurredAt)
            .ToListAsync(TestContext.Current.CancellationToken));

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
}
