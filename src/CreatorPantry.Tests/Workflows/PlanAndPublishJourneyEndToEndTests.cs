using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Publishing.Data.Entities;
using CreatorPantry.Domain.Modules.Publishing.Facade;
using CreatorPantry.Domain.Modules.Publishing.Gateways;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// "Plan and publish content" end to end through the real Gateway (13A.22), in the order the guided screens send it:
/// a run started from a board card, the schedule step held until the workspace has a scheduling zone, a booking
/// whose provider never answered — replayed under its key as the same record, never a second — the last step not
/// done while nobody knows, then done once reconciliation hears from the provider, and a cancellation — with a
/// neighbouring workspace that reaches none of it.
/// </summary>
/// <remarks>
/// The screens are the card page's own schedule form and publishing panel, inline; every request here is one they
/// already send. The workflow is only asked whether a step is done, and it reads the record's own state. The provider
/// is a scripted stand-in for the post gateway; reconciliation is run by hand, as its worker would run it.
/// </remarks>
public sealed class PlanAndPublishJourneyEndToEndTests : IAsyncLifetime
{
    private const string Zone = "America/Chicago";
    private const string Post = "Plum galette for the weekend, jammy and easy to love.";

    private readonly ScriptedProvider _provider = new();
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            services.RemoveAll<IPublishingPostGateway>();
            services.AddSingleton<IPublishingPostGateway>(_provider);
        });
        _provider.WorkspaceId = _fixture.WorkspaceA.Id;
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SeededWorkspace A => _fixture.WorkspaceA;

    private SeededWorkspace B => _fixture.WorkspaceB;

    private static string In(SeededWorkspace workspace, string path) => $"/api/v1/workspaces/{workspace.Slug}/{path}";

    private static string Key() => Guid.NewGuid().ToString("N");

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    [Fact]
    public async Task A_card_is_booked_in_the_workspace_zone_said_honestly_while_unknown_and_cancelled_and_the_neighbour_reaches_none_of_it()
    {
        var profileId = await SeedProfileAsync(A, zone: null);
        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        using var editor = await _fixture.SignInAsync(A.MemberEmail, cancellationToken: Ct);
        Assert.Equal(WorkspaceRole.Editor, A.MemberRole);

        var cardMade = await owner.PostAsJsonAsync(In(A, "content-cards"), new { title = "Plum galette week", column = "Approved" }, Key(), Ct);
        Assert.Equal(HttpStatusCode.Created, cardMade.StatusCode);
        var cardId = (await BodyOf(cardMade)).GetProperty("card").GetProperty("id").GetGuid();

        // ---- 1. Started from the card: choosing is done without choosing again ----
        var started = await owner.PostAsJsonAsync(
            In(A, "workflow-runs"),
            new { definitionKey = "publishing.plan-and-publish", startingResource = new { kind = "ContentCard", id = cardId } },
            Key(),
            Ct);
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var runId = (await BodyOf(started)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, A, runId, "choose")).StatusCode);

        // ---- 2. Checked: the creator's word ----
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, A, runId, "check")).StatusCode);
        Assert.Equal("schedule", NextStep(await ReadRunAsync(owner, A, runId)));

        // ---- 3. Schedule: held until there is a zone, and the booking refused without one ----
        var waiting = (await ReadRunAsync(owner, A, runId)).GetProperty("nextAction").GetProperty("waitingFor");
        Assert.NotEmpty(waiting.EnumerateArray());
        var noZone = await editor.PostAsync(In(A, "publishing/records"), JsonContent.Create(Booking(cardId, profileId)), Key(), Ct);
        Assert.NotEqual(HttpStatusCode.Created, noZone.StatusCode);
        Assert.Equal(0, _provider.Submits);

        await SetZoneAsync(A, Zone);
        Assert.Empty((await ReadRunAsync(owner, A, runId)).GetProperty("nextAction").GetProperty("waitingFor").EnumerateArray());

        // The provider never answers. The record says so; it does not round it off.
        _provider.Next = PublishingSubmissionResult.Of(PublishingSubmissionOutcome.Unknown, "provider_timeout", "No answer.");
        var booked = await editor.PostAsync(In(A, "publishing/records"), JsonContent.Create(Booking(cardId, profileId)), "plan-publish-book", Ct);
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var record = await BodyOf(booked);
        var recordId = record.GetProperty("id").GetGuid();
        Assert.True(record.GetProperty("outcomeUncertain").GetBoolean());
        Assert.Equal(Zone, record.GetProperty("scheduleTimeZoneId").GetString());
        Assert.Equal(LocalIntent(), record.GetProperty("scheduledLocal").GetString());

        // Sent again under its key — a lost answer, a second press — it is the same record, and nothing more is sent.
        var replay = await editor.PostAsync(In(A, "publishing/records"), JsonContent.Create(Booking(cardId, profileId)), "plan-publish-book", Ct);
        Assert.Equal(recordId, (await BodyOf(replay)).GetProperty("id").GetGuid());
        Assert.Equal(1, _provider.Submits);
        Assert.Single((await BodyOf(await owner.GetAsync(In(A, $"publishing/records?contentCardId={cardId}"), Ct))).GetProperty("items").EnumerateArray());

        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(editor, A, runId, "schedule", [new { kind = "PublishingRecord", id = recordId }])).StatusCode);

        // ---- 4. Confirm: not done while nobody knows ----
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CompleteAsync(owner, A, runId, "confirm")).StatusCode);

        // Reconciliation hears from the provider, and the record says what it heard.
        var dueAt = DateTimeOffset.Parse(Booking(cardId, profileId).ScheduledAt, System.Globalization.CultureInfo.InvariantCulture);
        _provider.Posts = [new ProviderPost("update_RECONCILED", Post, dueAt, PublishingPostState.Scheduled)];
        await ReconcileAsync(A, profileId, recordId);

        var settled = await BodyOf(await owner.GetAsync(In(A, $"publishing/records/{recordId}"), Ct));
        Assert.Equal("Scheduled", settled.GetProperty("status").GetString());
        Assert.False(settled.GetProperty("outcomeUncertain").GetBoolean());
        Assert.DoesNotContain("RECONCILED", settled.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("EXTERNAL", settled.GetRawText(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, A, runId, "confirm")).StatusCode);
        var finished = await owner.PostAsJsonAsync(
            In(A, $"workflow-runs/{runId}/completion"), new { expectedConcurrencyToken = await TokenAsync(owner, A, runId) }, Ct);
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        Assert.Equal("Completed", (await BodyOf(finished)).GetProperty("status").GetString());

        // ---- The neighbour: none of it is theirs to read, cancel or start from ----
        using var neighbour = await _fixture.SignInAsync(B.OwnerEmail, cancellationToken: Ct);
        var token = settled.GetProperty("concurrencyToken").GetString();
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.GetAsync(In(B, $"publishing/records/{recordId}"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.GetAsync(In(B, $"workflow-runs/{runId}"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.PostAsync(
            In(B, $"publishing/records/{recordId}/cancellation"), JsonContent.Create(new { expectedConcurrencyToken = token }), null, Ct)).StatusCode);
        Assert.Empty((await BodyOf(await neighbour.GetAsync(In(B, $"publishing/records?contentCardId={cardId}"), Ct))).GetProperty("items").EnumerateArray());
        var theirStart = await neighbour.PostAsJsonAsync(
            In(B, "workflow-runs"),
            new { definitionKey = "publishing.plan-and-publish", startingResource = new { kind = "ContentCard", id = cardId } },
            Key(),
            Ct);
        Assert.NotEqual(HttpStatusCode.Created, theirStart.StatusCode);

        // Our card and our profile book nothing in their workspace.
        var crossing = await neighbour.PostAsync(In(B, "publishing/records"), JsonContent.Create(Booking(cardId, profileId)), Key(), Ct);
        Assert.NotEqual(HttpStatusCode.Created, crossing.StatusCode);
        Assert.Equal(1, _provider.Submits);

        // A Viewer is refused the booking outright.
        using var viewer = await _fixture.SignInAsync(B.MemberEmail, cancellationToken: Ct);
        Assert.Equal(WorkspaceRole.Viewer, B.MemberRole);
        var viewed = await viewer.PostAsync(In(B, "publishing/records"), JsonContent.Create(Booking(Guid.NewGuid(), Guid.NewGuid())), Key(), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, viewed.StatusCode);

        // ---- Cancelling it, asked through the record's own route ----
        var cancelled = await editor.PostAsync(
            In(A, $"publishing/records/{recordId}/cancellation"), JsonContent.Create(new { expectedConcurrencyToken = token }), null, Ct);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Contains((await BodyOf(cancelled)).GetProperty("status").GetString(), new[] { "Cancelled", "Cancelling" });
    }

    [Fact]
    public async Task A_booking_the_provider_refused_leaves_the_last_step_not_done_and_a_new_booking_open()
    {
        var profileId = await SeedProfileAsync(A, zone: Zone);
        using var editor = await _fixture.SignInAsync(A.MemberEmail, cancellationToken: Ct);
        var cardId = (await BodyOf(await editor.PostAsJsonAsync(In(A, "content-cards"), new { title = "Soup notes" }, Key(), Ct)))
            .GetProperty("card").GetProperty("id").GetGuid();

        var runId = (await BodyOf(await editor.PostAsJsonAsync(
            In(A, "workflow-runs"),
            new { definitionKey = "publishing.plan-and-publish", startingResource = new { kind = "ContentCard", id = cardId } },
            Key(),
            Ct))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(editor, A, runId, "choose")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(editor, A, runId, "check")).StatusCode);

        _provider.Next = PublishingSubmissionResult.Of(PublishingSubmissionOutcome.Refused, "provider_rejected_input", "PROVIDER_PROSE");
        var refused = await BodyOf(await editor.PostAsync(In(A, "publishing/records"), JsonContent.Create(Booking(cardId, profileId)), Key(), Ct));
        Assert.Equal("Failed", refused.GetProperty("status").GetString());
        Assert.DoesNotContain("PROVIDER_PROSE", refused.GetRawText(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(editor, A, runId, "schedule", [new { kind = "PublishingRecord", id = refused.GetProperty("id").GetGuid() }])).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CompleteAsync(editor, A, runId, "confirm")).StatusCode);

        // Back to the schedule step, booked again, and this time it is taken.
        var back = await editor.PostAsJsonAsync(
            In(A, $"workflow-runs/{runId}/steps/confirm/back"), new { expectedConcurrencyToken = await TokenAsync(editor, A, runId) }, Ct);
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        _provider.Next = new PublishingSubmissionResult(PublishingSubmissionOutcome.Scheduled, "update_EXTERNAL", ProviderStatusCode: 200);
        var taken = await BodyOf(await editor.PostAsync(In(A, "publishing/records"), JsonContent.Create(Booking(cardId, profileId)), Key(), Ct));
        Assert.Equal("Scheduled", taken.GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(editor, A, runId, "schedule", [new { kind = "PublishingRecord", id = taken.GetProperty("id").GetGuid() }])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(editor, A, runId, "confirm")).StatusCode);
    }

    // ---- Helpers ----

    private sealed record BookingBody(Guid ContentCardId, Guid PublishingProfileId, string Text, string ScheduledAt, bool Confirmed);

    /// <summary>Half past eight in the morning in Chicago, three days ahead: inside the horizon on any day this runs.</summary>
    private static BookingBody Booking(Guid cardId, Guid profileId) => new(cardId, profileId, Post, When().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"), true);

    private static DateTimeOffset When()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(Zone);
        var day = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date.AddDays(3).AddHours(8.5);
        return new DateTimeOffset(day, zone.GetUtcOffset(day));
    }

    private static string LocalIntent() => When().ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    private static string? NextStep(JsonElement run) =>
        run.GetProperty("nextAction").ValueKind == JsonValueKind.Null
            ? null
            : run.GetProperty("nextAction").GetProperty("stepKey").GetString();

    private static async Task<JsonElement> ReadRunAsync(GatewayClient client, SeededWorkspace workspace, Guid runId) =>
        await BodyOf(await client.GetAsync(In(workspace, $"workflow-runs/{runId}"), Ct));

    private static async Task<string> TokenAsync(GatewayClient client, SeededWorkspace workspace, Guid runId) =>
        (await ReadRunAsync(client, workspace, runId)).GetProperty("concurrencyToken").GetString()!;

    private static async Task<HttpResponseMessage> CompleteAsync(
        GatewayClient client, SeededWorkspace workspace, Guid runId, string stepKey, object[]? results = null) =>
        await client.PostAsJsonAsync(
            In(workspace, $"workflow-runs/{runId}/steps/{stepKey}/completion"),
            new { results, expectedConcurrencyToken = await TokenAsync(client, workspace, runId) },
            Ct);

    /// <summary>A Ready profile with its provider mapping, and the brand profile that holds — or lacks — the zone.</summary>
    private async Task<Guid> SeedProfileAsync(SeededWorkspace workspace, string? zone)
    {
        var profileId = Guid.NewGuid();
        await InAsync(workspace, async db =>
        {
            db.PublishingProfiles.Add(new PublishingProfile
            {
                Id = profileId,
                ProviderKey = PublishingPolicy.BufferProviderKey,
                Platform = PublishingPlatform.Instagram,
                DisplayName = "The test kitchen",
                TimeZoneId = Zone,
                Status = PublishingProfileStatus.Ready,
                FirstSeenAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow,
            });
            db.ExternalPublishingProfileReferences.Add(new ExternalPublishingProfileReference
            {
                Id = Guid.NewGuid(),
                PublishingProfileId = profileId,
                ProviderKey = PublishingPolicy.BufferProviderKey,
                ExternalId = $"ch_EXTERNAL_{Guid.NewGuid():N}",
                ExternalAccountId = "org_EXTERNAL_1",
            });
            if (!await db.BrandProfiles.AnyAsync(Ct))
            {
                db.BrandProfiles.Add(new BrandProfile
                {
                    Id = Guid.NewGuid(),
                    BrandName = "The test kitchen",
                    TimeZoneId = zone,
                    Revision = 1,
                    CreatedByMembershipId = Guid.NewGuid(),
                    UpdatedByMembershipId = Guid.NewGuid(),
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }

            await db.SaveChangesAsync(Ct);
        });

        return profileId;
    }

    private Task SetZoneAsync(SeededWorkspace workspace, string zone) =>
        InAsync(workspace, async db =>
        {
            var brand = await db.BrandProfiles.SingleAsync(Ct);
            brand.TimeZoneId = zone;
            await db.SaveChangesAsync(Ct);
        });

    /// <summary>What the reconciliation worker does for one profile, run by hand in the workspace it resolves.</summary>
    private async Task ReconcileAsync(SeededWorkspace workspace, Guid profileId, Guid recordId)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "reconciliation");

        await scope.ServiceProvider.GetRequiredService<IPublishingReconciliationFacade>()
            .ReconcileProfileAsync(profileId, [recordId], Ct);
    }

    private async Task InAsync(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }

    /// <summary>The provider, as whatever the test says it answered.</summary>
    private sealed class ScriptedProvider : IPublishingPostGateway
    {
        private int _submits;

        public Guid? WorkspaceId { get; set; }

        public PublishingSubmissionResult Next { get; set; } =
            new(PublishingSubmissionOutcome.Scheduled, "update_EXTERNAL", ProviderStatusCode: 200);

        public IReadOnlyList<ProviderPost> Posts { get; set; } = [];

        public int Submits => _submits;

        public PublishingProviderDescriptor Describe() => new(true, PublishingPolicy.BufferProviderKey, WorkspaceId);

        public Task<PublishingSubmissionResult> SubmitAsync(PublishingSubmission submission, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _submits);
            return Task.FromResult(Next);
        }

        public Task<PublishingCancellationResult> CancelAsync(PublishingCancellation cancellation, CancellationToken cancellationToken) =>
            Task.FromResult(new PublishingCancellationResult(PublishingCancellationOutcome.Cancelled));

        public Task<PublishingPostLookupResult> ListPostsAsync(PublishingPostLookup lookup, CancellationToken cancellationToken) =>
            Task.FromResult(new PublishingPostLookupResult(PublishingPostLookupOutcome.Listed, Posts, Complete: true));
    }
}
