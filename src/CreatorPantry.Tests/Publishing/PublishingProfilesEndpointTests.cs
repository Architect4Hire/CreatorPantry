using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
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
/// <c>GET .../publishing/profiles</c> through the real Gateway (13.9, PUB-001): the shape, the degraded
/// answers, what is never returned, and the boundary between two workspaces.
/// </summary>
/// <remarks>
/// The provider is a scripted stand-in registered in place of the gateway. No test here calls Buffer; the
/// adapter is covered against recorded responses in <see cref="BufferPublishingProfileGatewayTests"/>.
/// </remarks>
public sealed class PublishingProfilesEndpointTests : IAsyncLifetime
{
    private const string ProviderSays = "PROVIDER_SAID_THIS_VERBATIM";

    private readonly ScriptedProvider _provider = new();
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            services.RemoveAll<IPublishingProfileGateway>();
            services.AddSingleton<IPublishingProfileGateway>(_provider);
        });

        // Bound to workspace A unless a test says otherwise.
        _provider.WorkspaceId = _fixture.WorkspaceA.Id;
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ProfilesOf(SeededWorkspace workspace, bool refresh = false) =>
        $"/api/v1/workspaces/{workspace.Slug}/publishing/profiles{(refresh ? "?refresh=true" : string.Empty)}";

    private Task<GatewayClient> SignInAsync(string email) => _fixture.SignInAsync(email, cancellationToken: Ct);

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static ProviderPublishingProfile Instagram() => new(
        "ch_EXTERNAL_ig", "org_EXTERNAL_1", PublishingPlatform.Instagram, "Cozy Fall Kitchen", "America/Chicago", PublishingProfileStatus.Ready);

    private static ProviderPublishingProfile Facebook() => new(
        "ch_EXTERNAL_fb", "org_EXTERNAL_1", PublishingPlatform.Facebook, "Cozy Fall", null, PublishingProfileStatus.NeedsReconnecting);

    // ---- The answer ----

    [Fact]
    public async Task A_workspace_reads_its_profiles_by_ids_of_our_own_with_nothing_of_the_providers_in_the_answer()
    {
        _provider.Next = new(PublishingProfileFetchOutcome.Fetched, [Facebook(), Instagram()]);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await client.GetAsync(ProfilesOf(_fixture.WorkspaceA), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var raw = await response.Content.ReadAsStringAsync(Ct);
        var body = JsonDocument.Parse(raw).RootElement;

        var connection = body.GetProperty("connection");
        Assert.Equal("buffer", connection.GetProperty("providerKey").GetString());
        Assert.Equal("Connected", connection.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, connection.GetProperty("lastCheckedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, connection.GetProperty("retryAfterSeconds").ValueKind);

        // By network and then name, whatever order the provider listed them in.
        var profiles = body.GetProperty("profiles").EnumerateArray().ToList();
        Assert.Equal(["Instagram", "Facebook"], profiles.Select(profile => profile.GetProperty("platform").GetString()));
        Assert.Equal("Cozy Fall Kitchen", profiles[0].GetProperty("displayName").GetString());
        Assert.Equal("America/Chicago", profiles[0].GetProperty("timeZoneId").GetString());
        Assert.Equal("Ready", profiles[0].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, profiles[1].GetProperty("timeZoneId").ValueKind);
        Assert.Equal("NeedsReconnecting", profiles[1].GetProperty("status").GetString());

        // An id of CreatorPantry's own, and exactly the fields that were promised.
        Assert.All(profiles, profile =>
        {
            Assert.NotEqual(Guid.Empty, profile.GetProperty("id").GetGuid());
            Assert.Equal(
                ["displayName", "id", "lastSeenAt", "platform", "providerKey", "status", "timeZoneId"],
                profile.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        });

        // No provider id, in any spelling.
        Assert.DoesNotContain("EXTERNAL", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("external", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("organization", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_viewer_may_read_and_somebody_signed_out_may_not()
    {
        _provider.WorkspaceId = _fixture.WorkspaceB.Id;
        _provider.Next = new(PublishingProfileFetchOutcome.Fetched, [Instagram()]);

        Assert.Equal(WorkspaceRole.Viewer, _fixture.WorkspaceB.MemberRole);
        using var viewer = await SignInAsync(_fixture.WorkspaceB.MemberEmail);
        var read = await viewer.GetAsync(ProfilesOf(_fixture.WorkspaceB), Ct);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(1, (await BodyOf(read)).GetProperty("profiles").GetArrayLength());

        using var anonymous = _fixture.Gateway.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(ProfilesOf(_fixture.WorkspaceB), Ct)).StatusCode);
    }

    // ---- Degraded, and still 200 ----

    [Fact]
    public async Task A_host_with_no_provider_answers_a_stable_not_configured_and_an_empty_list()
    {
        _provider.Configured = false;
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await client.GetAsync(ProfilesOf(_fixture.WorkspaceA), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal("NotConfigured", body.GetProperty("connection").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("connection").GetProperty("providerKey").ValueKind);
        Assert.Equal(0, body.GetProperty("profiles").GetArrayLength());
        Assert.Equal(0, _provider.Fetches);
    }

    [Theory]
    [InlineData(PublishingProfileFetchOutcome.Unavailable, "Unavailable")]
    [InlineData(PublishingProfileFetchOutcome.CredentialRejected, "CredentialRejected")]
    [InlineData(PublishingProfileFetchOutcome.RateLimited, "RateLimited")]
    public async Task A_provider_that_lets_the_read_down_is_a_status_on_a_200_and_never_an_error(
        PublishingProfileFetchOutcome outcome, string expected)
    {
        _provider.Next = PublishingProfileFetch.Of(outcome, outcome is PublishingProfileFetchOutcome.RateLimited ? TimeSpan.FromMinutes(30) : null);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await client.GetAsync(ProfilesOf(_fixture.WorkspaceA), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        var connection = JsonDocument.Parse(raw).RootElement.GetProperty("connection");
        Assert.Equal(expected, connection.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, connection.GetProperty("lastSucceededAt").ValueKind);

        // How long, in this server's own seconds; never a provider header or message relayed.
        Assert.True(connection.GetProperty("retryAfterSeconds").GetInt32() > 0);
        Assert.DoesNotContain(ProviderSays, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Profiles_read_earlier_are_still_listed_when_the_provider_goes_away()
    {
        _provider.Next = new(PublishingProfileFetchOutcome.Fetched, [Instagram()]);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var before = (await BodyOf(await client.GetAsync(ProfilesOf(_fixture.WorkspaceA), Ct))).GetProperty("profiles");

        _provider.Configured = false;
        var after = await BodyOf(await client.GetAsync(ProfilesOf(_fixture.WorkspaceA), Ct));

        Assert.Equal("NotConfigured", after.GetProperty("connection").GetProperty("status").GetString());
        Assert.Equal(before.GetRawText(), after.GetProperty("profiles").GetRawText());
    }

    // ---- Asked sparingly ----

    [Fact]
    public async Task Reading_again_and_asking_for_a_refresh_straight_away_do_not_ask_the_provider_twice()
    {
        _provider.Next = new(PublishingProfileFetchOutcome.Fetched, [Instagram()]);
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var first = await BodyOf(await client.GetAsync(ProfilesOf(_fixture.WorkspaceA), Ct));
        var again = await BodyOf(await client.GetAsync(ProfilesOf(_fixture.WorkspaceA), Ct));
        var refreshed = await BodyOf(await client.GetAsync(ProfilesOf(_fixture.WorkspaceA, refresh: true), Ct));

        Assert.Equal(1, _provider.Fetches);
        Assert.Equal(first.GetRawText(), again.GetRawText());
        Assert.Equal(first.GetProperty("profiles").GetRawText(), refreshed.GetProperty("profiles").GetRawText());
    }

    [Fact]
    public async Task A_viewers_refresh_is_served_the_last_read_and_never_reaches_the_provider()
    {
        _provider.WorkspaceId = _fixture.WorkspaceB.Id;
        _provider.Next = new(PublishingProfileFetchOutcome.Fetched, [Instagram()]);
        using var owner = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        var read = await BodyOf(await owner.GetAsync(ProfilesOf(_fixture.WorkspaceB), Ct));

        // Long enough ago that a refresh from somebody allowed one would be honoured.
        await AgeLastReadAsync(_fixture.WorkspaceB, TimeSpan.FromMinutes(5));

        Assert.Equal(WorkspaceRole.Viewer, _fixture.WorkspaceB.MemberRole);
        using var viewer = await SignInAsync(_fixture.WorkspaceB.MemberEmail);
        var asked = await viewer.GetAsync(ProfilesOf(_fixture.WorkspaceB, refresh: true), Ct);

        // Not refused, and not obeyed: the quota is not a Viewer's to spend.
        Assert.Equal(HttpStatusCode.OK, asked.StatusCode);
        Assert.Equal(1, _provider.Fetches);
        Assert.Equal(read.GetProperty("profiles").GetRawText(), (await BodyOf(asked)).GetProperty("profiles").GetRawText());

        // The same request from the Owner is.
        await owner.GetAsync(ProfilesOf(_fixture.WorkspaceB, refresh: true), Ct);
        Assert.Equal(2, _provider.Fetches);
    }

    // ---- Two workspaces ----

    [Fact]
    public async Task The_workspace_the_key_does_not_belong_to_reads_exactly_what_a_host_with_no_provider_says()
    {
        _provider.Next = new(PublishingProfileFetchOutcome.Fetched, [Instagram(), Facebook()]);
        using var inA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var inB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        // A has read its profiles; the rows exist.
        Assert.Equal(2, (await BodyOf(await inA.GetAsync(ProfilesOf(_fixture.WorkspaceA), Ct))).GetProperty("profiles").GetArrayLength());
        var fetchesAfterA = _provider.Fetches;

        var foreign = await inB.GetAsync(ProfilesOf(_fixture.WorkspaceB, refresh: true), Ct);
        var foreignBody = await foreign.Content.ReadAsStringAsync(Ct);

        // What a deployment with no provider at all answers, byte for byte.
        _provider.Configured = false;
        var bare = await (await inB.GetAsync(ProfilesOf(_fixture.WorkspaceB), Ct)).Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, foreign.StatusCode);
        Assert.Equal(bare, foreignBody);
        Assert.DoesNotContain("buffer", foreignBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cozy Fall", foreignBody, StringComparison.Ordinal);

        // B's read never reached the provider, and wrote nothing.
        Assert.Equal(fetchesAfterA, _provider.Fetches);
        Assert.Equal((0, 0, 0), await StoredAsync(_fixture.WorkspaceB));
        Assert.Equal((2, 2, 1), await StoredAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task One_workspaces_member_cannot_read_anothers_profiles_by_naming_its_route()
    {
        _provider.Next = new(PublishingProfileFetchOutcome.Fetched, [Instagram()]);
        using var inA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var inB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        await inA.GetAsync(ProfilesOf(_fixture.WorkspaceA), Ct);

        // B's owner on A's route, and on a route for a workspace nobody has: the same refusal.
        var foreign = await inB.GetAsync(ProfilesOf(_fixture.WorkspaceA), Ct);
        var unknown = await inB.GetAsync("/api/v1/workspaces/no-such-workspace/publishing/profiles", Ct);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(
            (await BodyOf(foreign)).GetProperty("code").GetString(),
            (await BodyOf(unknown)).GetProperty("code").GetString());
        Assert.DoesNotContain("Cozy Fall", await foreign.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    // ---- Helpers ----

    /// <summary>Profiles, provider mappings and sync records one workspace holds.</summary>
    private async Task<(int Profiles, int References, int Syncs)> StoredAsync(SeededWorkspace workspace)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        return (
            await db.PublishingProfiles.CountAsync(Ct),
            await db.ExternalPublishingProfileReferences.CountAsync(Ct),
            await db.PublishingProviderSyncs.CountAsync(Ct));
    }

    /// <summary>Moves a workspace's last read into the past, as time passing would.</summary>
    private async Task AgeLastReadAsync(SeededWorkspace workspace, TimeSpan by)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var sync = await db.PublishingProviderSyncs.SingleAsync(Ct);
        sync.LastAttemptAt -= by;
        sync.LastSucceededAt -= by;
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>A provider that answers what the test says, for the workspace the test says.</summary>
    private sealed class ScriptedProvider : IPublishingProfileGateway
    {
        private int _fetches;

        public bool Configured { get; set; } = true;

        public Guid? WorkspaceId { get; set; }

        public PublishingProfileFetch Next { get; set; } = new(PublishingProfileFetchOutcome.Fetched, []);

        public int Fetches => _fetches;

        public PublishingProviderDescriptor Describe() =>
            Configured ? new(true, PublishingPolicy.BufferProviderKey, WorkspaceId) : PublishingProviderDescriptor.None;

        public Task<PublishingProfileFetch> FetchAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _fetches);

            return Task.FromResult(Next);
        }
    }
}
