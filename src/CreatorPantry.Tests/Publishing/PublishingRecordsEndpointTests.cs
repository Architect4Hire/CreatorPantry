using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Publishing.Data.Entities;
using CreatorPantry.Domain.Modules.Publishing.Gateways;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Publishing;

/// <summary>
/// <c>POST .../publishing/records</c> and its read, through the real Gateway (13.10a, PUB-002): the contract,
/// the role, the replay header, and what is never in the answer.
/// </summary>
/// <remarks>
/// <para>
/// The provider is a scripted stand-in registered in place of the post gateway. No test here calls Buffer; the
/// adapter is covered against recorded responses in <see cref="BufferPublishingPostGatewayTests"/>.
/// </para>
/// <para>
/// This host is SQLite, where a row version is given a value on insert but not bumped on update. So a token
/// going <em>stale</em> is asserted against SQL Server, in <c>PublishingScheduleTests</c>; what is asserted
/// here is that the route demands one and refuses a value it never issued.
/// </para>
/// </remarks>
public sealed class PublishingRecordsEndpointTests : IAsyncLifetime
{
    private const string ProviderPostId = "update_EXTERNAL_MUST_NOT_APPEAR";
    private const string Zone = "America/Chicago";

    private readonly ScriptedProvider _provider = new();
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            services.RemoveAll<IPublishingPostGateway>();
            services.AddSingleton<IPublishingPostGateway>(_provider);
        });

        // The credential belongs to workspace A unless a test says otherwise.
        _provider.WorkspaceId = _fixture.WorkspaceA.Id;
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- The contract ----

    [Fact]
    public async Task An_editor_confirms_a_publication_and_gets_the_record_and_its_location()
    {
        var (cardId, profileId) = await SeedAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        var response = await PostAsync(client, _fixture.WorkspaceA, Confirmation(cardId, profileId), "key-1");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains(IdempotencyPolicy.ReplayedHeader));

        var raw = await response.Content.ReadAsStringAsync(Ct);
        var body = JsonDocument.Parse(raw).RootElement;

        Assert.Equal("Scheduled", body.GetProperty("status").GetString());
        Assert.Equal("Instagram", body.GetProperty("platform").GetString());
        Assert.Equal(profileId, body.GetProperty("publishingProfileId").GetGuid());
        Assert.Equal(cardId, body.GetProperty("contentCardId").GetGuid());
        Assert.Equal(Zone, body.GetProperty("scheduleTimeZoneId").GetString());
        Assert.False(body.GetProperty("outcomeUncertain").GetBoolean());
        Assert.Equal(1, body.GetProperty("submitAttempts").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("concurrencyToken").ValueKind);

        // The local intent, with no offset of its own: eight in the morning in Chicago.
        Assert.Equal("2026-10-16T08:30:00", body.GetProperty("scheduledLocal").GetString());

        var location = response.Headers.Location!.ToString();
        Assert.Equal(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/publishing/records/{body.GetProperty("id").GetGuid()}",
            location);

        // The provider's id for the post is in the mapping record and never in an answer.
        Assert.DoesNotContain("EXTERNAL", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_location_it_gives_is_a_route_that_reads_the_record_back()
    {
        var (cardId, profileId) = await SeedAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        var created = await PostAsync(client, _fixture.WorkspaceA, Confirmation(cardId, profileId), "key-2");
        var location = created.Headers.Location!.ToString();

        var read = await client.GetAsync(location, Ct);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("no-store", read.Headers.CacheControl?.ToString());

        var body = await BodyOf(read);
        Assert.Equal("Scheduled", body.GetProperty("status").GetString());
        Assert.DoesNotContain("EXTERNAL", await read.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_retry_with_the_same_key_answers_the_first_record_and_says_it_was_a_replay()
    {
        var (cardId, profileId) = await SeedAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.MemberEmail);
        var confirmation = Confirmation(cardId, profileId);

        var first = await PostAsync(client, _fixture.WorkspaceA, confirmation, "key-3");
        var replay = await PostAsync(client, _fixture.WorkspaceA, confirmation, "key-3");

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues(IdempotencyPolicy.ReplayedHeader).Single());
        Assert.Equal(
            (await BodyOf(first)).GetProperty("id").GetGuid(),
            (await BodyOf(replay)).GetProperty("id").GetGuid());

        // The whole point: one key, one post.
        Assert.Equal(1, _provider.Submits);
    }

    [Fact]
    public async Task A_confirmation_without_an_idempotency_key_is_refused_and_nothing_is_sent()
    {
        var (cardId, profileId) = await SeedAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        var response = await PostAsync(client, _fixture.WorkspaceA, Confirmation(cardId, profileId), key: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The shared idempotency contract's own code, so this command refuses a missing key exactly as every
        // other idempotent command in the product does.
        Assert.Equal(
            IdempotencyPolicy.KeyRequiredCode, (await BodyOf(response)).GetProperty("code").GetString());
        Assert.Equal(0, _provider.Submits);
    }

    [Fact]
    public async Task A_body_without_the_creators_confirmation_is_refused()
    {
        var (cardId, profileId) = await SeedAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        var response = await PostAsync(
            client,
            _fixture.WorkspaceA,
            new
            {
                contentCardId = cardId,
                publishingProfileId = profileId,
                text = "Soda bread.",
                scheduledAt = Confirmed(),
                confirmed = false,
            },
            "key-4");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await BodyOf(response)).GetProperty("errors").TryGetProperty("confirmed", out _));
        Assert.Equal(0, _provider.Submits);
    }

    // ---- Who may ----

    [Fact]
    public async Task A_viewer_may_not_confirm_a_publication()
    {
        var (cardId, profileId) = await SeedAsync(_fixture.WorkspaceB);
        using var client = await SignInAsync(_fixture.WorkspaceB.MemberEmail);

        var response = await PostAsync(client, _fixture.WorkspaceB, Confirmation(cardId, profileId), "key-5");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, _provider.Submits);
    }

    [Fact]
    public async Task A_viewer_may_read_a_record()
    {
        var (cardId, profileId) = await SeedAsync(_fixture.WorkspaceA);
        using var editor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);
        var created = await PostAsync(editor, _fixture.WorkspaceA, Confirmation(cardId, profileId), "key-6");
        var recordId = (await BodyOf(created)).GetProperty("id").GetGuid();

        // Workspace A's owner reads it; the role floor on the read is the lowest one.
        using var owner = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var read = await owner.GetAsync(RecordOf(_fixture.WorkspaceA, recordId), Ct);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    // ---- Two workspaces ----

    [Fact]
    public async Task A_workspace_that_is_not_the_credentials_own_cannot_schedule_and_learns_nothing_about_it()
    {
        var (cardId, profileId) = await SeedAsync(_fixture.WorkspaceB);
        using var client = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        var response = await PostAsync(client, _fixture.WorkspaceB, Confirmation(cardId, profileId), "key-7");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            PublishingErrorCodes.ProviderNotConfigured,
            (await BodyOf(response)).GetProperty("code").GetString());

        // The same answer a deployment with no provider at all gives: nothing here reveals that A has one.
        Assert.Equal(0, _provider.Submits);
    }

    [Fact]
    public async Task Another_workspaces_record_is_not_found_here()
    {
        var (cardId, profileId) = await SeedAsync(_fixture.WorkspaceA);
        using var editor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);
        var created = await PostAsync(editor, _fixture.WorkspaceA, Confirmation(cardId, profileId), "key-8");
        var recordId = (await BodyOf(created)).GetProperty("id").GetGuid();

        using var neighbour = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        var response = await neighbour.GetAsync(RecordOf(_fixture.WorkspaceB, recordId), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            PublishingErrorCodes.RecordNotFound,
            (await BodyOf(response)).GetProperty("code").GetString());

        // And an id nobody has answers exactly the same, so neither discloses the other.
        var unknown = await neighbour.GetAsync(RecordOf(_fixture.WorkspaceB, Guid.NewGuid()), Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task A_card_from_another_workspace_is_refused()
    {
        var (theirCard, _) = await SeedAsync(_fixture.WorkspaceB);
        var (_, profileId) = await SeedAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        var response = await PostAsync(client, _fixture.WorkspaceA, Confirmation(theirCard, profileId), "key-9");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await BodyOf(response)).GetProperty("errors").TryGetProperty("contentCardId", out _));
        Assert.Equal(0, _provider.Submits);
    }

    // ---- What the provider's answer does to the response ----

    [Fact]
    public async Task A_provider_that_did_not_answer_gives_a_record_that_says_the_outcome_is_unknown()
    {
        _provider.Next = PublishingSubmissionResult.Of(
            PublishingSubmissionOutcome.Unknown, "provider_timeout", "No answer.");
        var (cardId, profileId) = await SeedAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        var response = await PostAsync(client, _fixture.WorkspaceA, Confirmation(cardId, profileId), "key-10");

        // Still a 201: the confirmation is recorded. The state is where the honesty lives.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal("Submitting", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("outcomeUncertain").GetBoolean());
        Assert.Equal("provider_timeout", body.GetProperty("failureCategory").GetString());
    }

    [Fact]
    public async Task A_refused_post_gives_a_failed_record_and_no_word_of_the_providers_own()
    {
        const string providerSays = "PROVIDER_PROSE_THAT_MUST_NOT_TRAVEL";
        _provider.Next = PublishingSubmissionResult.Of(
            PublishingSubmissionOutcome.Refused, "provider_rejected_input", providerSays);
        var (cardId, profileId) = await SeedAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        var response = await PostAsync(client, _fixture.WorkspaceA, Confirmation(cardId, profileId), "key-11");

        var raw = await response.Content.ReadAsStringAsync(Ct);
        var body = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Failed", body.GetProperty("status").GetString());
        Assert.Equal("provider_rejected_input", body.GetProperty("failureCategory").GetString());

        // What the name of this test claims, asserted: the summary is composed on this server from the
        // category, so even a gateway that handed up a provider's prose could not get it into a response.
        Assert.DoesNotContain(providerSays, raw, StringComparison.Ordinal);
        Assert.Equal(
            "The publishing provider would not accept this post as written.",
            body.GetProperty("failureSummary").GetString());
    }

    // ---- Reading a card's publications ----

    [Fact]
    public async Task A_cards_publications_are_readable_by_any_member_and_carry_nothing_of_the_providers()
    {
        var (cardId, profileId) = await SeedAsync(_fixture.WorkspaceA);
        using var editor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);
        var created = await BodyOf(
            await PostAsync(editor, _fixture.WorkspaceA, Confirmation(cardId, profileId), "key-16"));

        using var owner = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await owner.GetAsync(
            $"{RecordsOf(_fixture.WorkspaceA)}?contentCardId={cardId}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var raw = await response.Content.ReadAsStringAsync(Ct);
        var body = JsonDocument.Parse(raw).RootElement;

        Assert.True(body.GetProperty("complete").GetBoolean());
        var item = Assert.Single(body.GetProperty("items").EnumerateArray().ToList());
        Assert.Equal(created.GetProperty("id").GetGuid(), item.GetProperty("id").GetGuid());
        Assert.Equal("Scheduled", item.GetProperty("status").GetString());

        Assert.DoesNotContain("EXTERNAL", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_workspaces_card_has_no_publications_here()
    {
        var (cardId, profileId) = await SeedAsync(_fixture.WorkspaceA);
        using var editor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);
        await PostAsync(editor, _fixture.WorkspaceA, Confirmation(cardId, profileId), "key-17");

        using var neighbour = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        var response = await neighbour.GetAsync(
            $"{RecordsOf(_fixture.WorkspaceB)}?contentCardId={cardId}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await BodyOf(response)).GetProperty("items").EnumerateArray().ToList());
    }

    [Fact]
    public async Task Reading_publications_without_naming_a_card_is_refused_rather_than_answered_empty()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        var response = await client.GetAsync(RecordsOf(_fixture.WorkspaceA), Ct);

        // An empty list that calls itself complete is the one answer this must never give by accident: it
        // reads as "this card has nothing scheduled", which is exactly what the design exists to protect.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            PublishingErrorCodes.ScheduleInvalid, (await BodyOf(response)).GetProperty("code").GetString());
    }

    // ---- Cancelling ----

    [Fact]
    public async Task An_editor_cancels_a_publication_and_the_record_says_it_is_cancelled()
    {
        var (cardId, profileId) = await SeedAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.MemberEmail);
        var created = await PostAsync(client, _fixture.WorkspaceA, Confirmation(cardId, profileId), "key-12");
        var record = await BodyOf(created);

        var response = await CancelAsync(
            client, _fixture.WorkspaceA, record.GetProperty("id").GetGuid(),
            record.GetProperty("concurrencyToken").GetString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var body = await BodyOf(response);
        Assert.Equal("Cancelled", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("outcomeUncertain").GetBoolean());
        Assert.Equal(1, _provider.Removals);
    }

    [Fact]
    public async Task Cancelling_twice_answers_the_same_way_and_asks_the_provider_once()
    {
        var (cardId, profileId) = await SeedAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.MemberEmail);
        var created = await BodyOf(
            await PostAsync(client, _fixture.WorkspaceA, Confirmation(cardId, profileId), "key-13"));
        var recordId = created.GetProperty("id").GetGuid();

        var first = await BodyOf(await CancelAsync(
            client, _fixture.WorkspaceA, recordId, created.GetProperty("concurrencyToken").GetString()));

        var again = await CancelAsync(
            client, _fixture.WorkspaceA, recordId, first.GetProperty("concurrencyToken").GetString());

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("Cancelled", (await BodyOf(again)).GetProperty("status").GetString());
        Assert.Equal(1, _provider.Removals);
    }

    [Fact]
    public async Task A_cancellation_quoting_a_token_this_api_never_issued_is_refused()
    {
        var (cardId, profileId) = await SeedAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA.MemberEmail);
        var created = await BodyOf(
            await PostAsync(client, _fixture.WorkspaceA, Confirmation(cardId, profileId), "key-14"));

        var response = await CancelAsync(
            client, _fixture.WorkspaceA, created.GetProperty("id").GetGuid(), "AAAAAAAAB9E=");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            PublishingErrorCodes.RecordConflict, (await BodyOf(response)).GetProperty("code").GetString());
        Assert.Equal(0, _provider.Removals);
    }

    [Fact]
    public async Task A_viewer_may_not_cancel_a_publication()
    {
        using var client = await SignInAsync(_fixture.WorkspaceB.MemberEmail);

        var response = await CancelAsync(client, _fixture.WorkspaceB, Guid.NewGuid(), "AAAAAAAAB9E=");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, _provider.Removals);
    }

    [Fact]
    public async Task Another_workspaces_publication_cannot_be_cancelled_from_here()
    {
        var (cardId, profileId) = await SeedAsync(_fixture.WorkspaceA);
        using var editor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);
        var created = await BodyOf(
            await PostAsync(editor, _fixture.WorkspaceA, Confirmation(cardId, profileId), "key-15"));

        using var neighbour = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        var response = await CancelAsync(
            neighbour, _fixture.WorkspaceB, created.GetProperty("id").GetGuid(),
            created.GetProperty("concurrencyToken").GetString());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, _provider.Removals);
    }

    // ---- Helpers ----

    private static object Confirmation(Guid cardId, Guid profileId) => new
    {
        contentCardId = cardId,
        publishingProfileId = profileId,
        text = "Soda bread, out of the oven at last.",
        scheduledAt = Confirmed(),
        confirmed = true,
    };

    /// <summary>Eight thirty in the morning in Chicago, well inside the horizon.</summary>
    private static string Confirmed() => "2026-10-16T13:30:00Z";

    private static string RecordsOf(SeededWorkspace workspace) =>
        $"/api/v1/workspaces/{workspace.Slug}/publishing/records";

    private static string RecordOf(SeededWorkspace workspace, Guid recordId) =>
        $"{RecordsOf(workspace)}/{recordId}";

    private Task<GatewayClient> SignInAsync(string email) => _fixture.SignInAsync(email, cancellationToken: Ct);

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static Task<HttpResponseMessage> PostAsync(
        GatewayClient client, SeededWorkspace workspace, object body, string? key) =>
        client.PostAsync(RecordsOf(workspace), JsonContent.Create(body), key, Ct);

    private static Task<HttpResponseMessage> CancelAsync(
        GatewayClient client, SeededWorkspace workspace, Guid recordId, string? token) =>
        client.PostAsync(
            $"{RecordOf(workspace, recordId)}/cancellation",
            JsonContent.Create(new { expectedConcurrencyToken = token }),
            idempotencyKey: null,
            Ct);

    /// <summary>A card, a Ready profile with its provider mapping, and a time zone to schedule in.</summary>
    private async Task<(Guid CardId, Guid ProfileId)> SeedAsync(SeededWorkspace workspace)
    {
        var cardId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        await InAsync(workspace, async db =>
        {
            db.ContentCards.Add(new ContentCard
            {
                Id = cardId,
                Title = "Soda bread, Friday",
                Column = ContentCardColumn.Approved,
                SortRank = ContentCardPolicy.RankGap,
                CreatedByMembershipId = Guid.NewGuid(),
                UpdatedByMembershipId = Guid.NewGuid(),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

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

            // The scheduling zone the command reads, and refuses without.
            if (!await db.BrandProfiles.AnyAsync(Ct))
            {
                db.BrandProfiles.Add(new BrandProfile
                {
                    Id = Guid.NewGuid(),
                    BrandName = "The test kitchen",
                    TimeZoneId = Zone,
                    Revision = 1,
                    CreatedByMembershipId = Guid.NewGuid(),
                    UpdatedByMembershipId = Guid.NewGuid(),
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }

            await db.SaveChangesAsync(Ct);
        });

        return (cardId, profileId);
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
        private int _removals;

        public Guid? WorkspaceId { get; set; }

        public PublishingSubmissionResult Next { get; set; } =
            new(PublishingSubmissionOutcome.Scheduled, ProviderPostId, ProviderStatusCode: 200);

        public int Submits => _submits;

        public PublishingProviderDescriptor Describe() =>
            new(true, PublishingPolicy.BufferProviderKey, WorkspaceId);

        public PublishingCancellationResult NextCancellation { get; set; } =
            new(PublishingCancellationOutcome.Cancelled);

        public int Removals => _removals;

        public Task<PublishingSubmissionResult> SubmitAsync(
            PublishingSubmission submission, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _submits);

            return Task.FromResult(Next);
        }

        public Task<PublishingCancellationResult> CancelAsync(
            PublishingCancellation cancellation, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _removals);

            return Task.FromResult(NextCancellation);
        }

        public Task<PublishingPostLookupResult> ListPostsAsync(
            PublishingPostLookup lookup, CancellationToken cancellationToken) =>
            Task.FromResult(new PublishingPostLookupResult(PublishingPostLookupOutcome.Listed, [], Complete: true));
    }
}
