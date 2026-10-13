using System.Net;
using System.Text;
using System.Text.Json;
using CreatorPantry.Domain.Modules.Publishing.Gateways;
using CreatorPantry.Domain.Modules.Publishing.Integration.Buffer;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using CreatorPantry.Tests.Auth;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Tests.Publishing;

/// <summary>
/// The Buffer adapter against recorded responses (13.9, INT-030): the mapping, every way Buffer can decline,
/// what is retried and what is not, and what never reaches a log.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No test here calls Buffer.</strong> Every response is a fixture written from Buffer's published
/// contract (developers.buffer.com, as relaunched 27 May 2026), replayed through a stub handler. The shapes
/// that matter are the ones the documentation states: errors arrive as HTTP 200 with an <c>errors</c> array
/// and an <c>extensions.code</c>; a throttle is a 429 with <c>Retry-After</c>.
/// </para>
/// <para>
/// The key and the ids below are made up and recognisable, so a leak assertion can look for them by name.
/// </para>
/// </remarks>
public sealed class BufferPublishingProfileGatewayTests
{
    private const string ApiKey = "bfr_SECRET_KEY_do_not_log_4f1c";
    private const string OrgOne = "org_EXTERNAL_one";
    private const string OrgTwo = "org_EXTERNAL_two";
    private const string ProviderSays = "PROVIDER_SAID_THIS_VERBATIM";
    private static readonly Guid Workspace = Guid.Parse("7d0c1f0e-52a4-4c1b-9a35-3d5b6c1f0a11");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Recorded responses ----

    private static string Organizations(params string[] ids) =>
        JsonSerializer.Serialize(new { data = new { account = new { organizations = ids.Select(id => new { id }) } } });

    private static string Channels(params object[] channels) => JsonSerializer.Serialize(new { data = new { channels } });

    private static object Channel(
        string id,
        string service,
        string? displayName = "Cozy Fall Kitchen",
        string name = "cozyfall",
        string? timezone = "America/Chicago",
        bool isDisconnected = false,
        bool isLocked = false,
        string organizationId = OrgOne) =>
        new { id, name, displayName, service, timezone, isDisconnected, isLocked, organizationId };

    private static string Error(string code) => JsonSerializer.Serialize(new
    {
        data = (object?)null,
        errors = new[] { new { message = ProviderSays, extensions = new { code } } },
    });

    private static Scripted Ok(string body) => new(HttpStatusCode.OK, body);

    private static Scripted Status(HttpStatusCode status, string body = "", int? retryAfterSeconds = null) =>
        new(status, body, retryAfterSeconds);

    // ---- The read ----

    [Fact]
    public async Task It_reads_every_organizations_channels_and_maps_them_to_the_applications_own_terms()
    {
        var buffer = new StubBuffer(
            Ok(Organizations(OrgOne, OrgTwo)),
            Ok(Channels(
                Channel("ch_EXTERNAL_1", "instagram"),
                Channel("ch_EXTERNAL_2", "twitter", displayName: null, name: "cozyfall_x", timezone: null))),
            Ok(Channels(
                Channel("ch_EXTERNAL_3", "facebook", isDisconnected: true, organizationId: OrgTwo),
                Channel("ch_EXTERNAL_4", "linkedin", isLocked: true, organizationId: OrgTwo))));

        var result = await buffer.Gateway().FetchAsync(Ct);

        Assert.Equal(PublishingProfileFetchOutcome.Fetched, result.Outcome);
        Assert.Equal(
            [
                new ProviderPublishingProfile("ch_EXTERNAL_1", OrgOne, PublishingPlatform.Instagram, "Cozy Fall Kitchen", "America/Chicago", PublishingProfileStatus.Ready),

                // No display name: the handle stands in. No zone: none is invented.
                new ProviderPublishingProfile("ch_EXTERNAL_2", OrgOne, PublishingPlatform.X, "cozyfall_x", null, PublishingProfileStatus.Ready),
                new ProviderPublishingProfile("ch_EXTERNAL_3", OrgTwo, PublishingPlatform.Facebook, "Cozy Fall Kitchen", "America/Chicago", PublishingProfileStatus.NeedsReconnecting),
                new ProviderPublishingProfile("ch_EXTERNAL_4", OrgTwo, PublishingPlatform.LinkedIn, "Cozy Fall Kitchen", "America/Chicago", PublishingProfileStatus.Locked),
            ],
            result.Profiles);
    }

    [Fact]
    public async Task Each_request_is_a_post_of_a_query_to_the_one_endpoint_and_carries_the_organization_as_a_variable()
    {
        var buffer = new StubBuffer(Ok(Organizations(OrgOne)), Ok(Channels(Channel("ch_EXTERNAL_1", "instagram"))));

        await buffer.Gateway().FetchAsync(Ct);

        Assert.Equal(2, buffer.Requests.Count);
        Assert.All(buffer.Requests, request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.buffer.example/", request.Uri);

            // Read-only: neither request is a mutation.
            Assert.StartsWith("query", request.Body.GetProperty("query").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain("mutation", request.Raw, StringComparison.OrdinalIgnoreCase);

            // The key is the client's header, set by the host. It is never in a body.
            Assert.DoesNotContain(ApiKey, request.Raw, StringComparison.Ordinal);
        });

        var channels = buffer.Requests[1].Body;
        Assert.Equal(OrgOne, channels.GetProperty("variables").GetProperty("input").GetProperty("organizationId").GetString());

        // Spliced nowhere into the query text.
        Assert.DoesNotContain(OrgOne, channels.GetProperty("query").GetString()!, StringComparison.Ordinal);

        // Only what is mapped is asked for. An avatar is an address Buffer chose; it is not fetched or relayed.
        Assert.DoesNotContain("avatar", channels.GetProperty("query").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("instagram", PublishingPlatform.Instagram)]
    [InlineData("facebook", PublishingPlatform.Facebook)]
    [InlineData("twitter", PublishingPlatform.X)]
    [InlineData("linkedin", PublishingPlatform.LinkedIn)]
    [InlineData("pinterest", PublishingPlatform.Pinterest)]
    [InlineData("tiktok", PublishingPlatform.TikTok)]
    [InlineData("youtube", PublishingPlatform.YouTube)]
    [InlineData("mastodon", PublishingPlatform.Mastodon)]
    [InlineData("bluesky", PublishingPlatform.Bluesky)]
    [InlineData("threads", PublishingPlatform.Threads)]
    [InlineData("whatsapp", PublishingPlatform.WhatsApp)]
    [InlineData("INSTAGRAM", PublishingPlatform.Instagram)]
    [InlineData("a_network_added_next_year", PublishingPlatform.Other)]
    [InlineData(null, PublishingPlatform.Other)]
    public void Every_network_buffer_documents_has_a_platform_and_one_it_adds_later_is_other(string? service, PublishingPlatform expected) =>
        Assert.Equal(expected, BufferPublishingProfileGateway.PlatformOf(service));

    [Fact]
    public async Task An_account_with_no_organizations_or_no_channels_is_an_empty_list_and_not_a_failure()
    {
        var none = await new StubBuffer(Ok(Organizations())).Gateway().FetchAsync(Ct);
        Assert.Equal(PublishingProfileFetchOutcome.Fetched, none.Outcome);
        Assert.Empty(none.Profiles);

        var empty = await new StubBuffer(Ok(Organizations(OrgOne)), Ok(Channels())).Gateway().FetchAsync(Ct);
        Assert.Equal(PublishingProfileFetchOutcome.Fetched, empty.Outcome);
        Assert.Empty(empty.Profiles);
    }

    // ---- Refusals: HTTP 200 is not success ----

    [Theory]
    [InlineData("UNAUTHORIZED")]
    [InlineData("FORBIDDEN")]
    public async Task A_refused_key_arrives_as_http_200_and_is_a_rejected_credential_asked_once(string code)
    {
        var buffer = new StubBuffer(Ok(Error(code)));

        var result = await buffer.Gateway().FetchAsync(Ct);

        Assert.Equal(PublishingProfileFetchOutcome.CredentialRejected, result.Outcome);
        Assert.Empty(result.Profiles);

        // Asking again with the same key gets the same answer.
        Assert.Single(buffer.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_refusal_by_status_is_read_the_same_way(HttpStatusCode status)
    {
        var buffer = new StubBuffer(Status(status));

        Assert.Equal(PublishingProfileFetchOutcome.CredentialRejected, (await buffer.Gateway().FetchAsync(Ct)).Outcome);
        Assert.Single(buffer.Requests);
    }

    // ---- Throttling ----

    [Fact]
    public async Task A_throttle_is_reported_with_how_long_buffer_asked_for_and_is_not_waited_out()
    {
        var buffer = new StubBuffer(Status(HttpStatusCode.TooManyRequests, Error("RATE_LIMIT_EXCEEDED"), retryAfterSeconds: 900));

        var result = await buffer.Gateway().FetchAsync(Ct);

        Assert.Equal(PublishingProfileFetchOutcome.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(900), result.RetryAfter);
        Assert.Single(buffer.Requests);
    }

    [Fact]
    public async Task A_throttle_that_asks_for_a_moment_is_waited_out_once_and_then_once_only()
    {
        var recovers = new StubBuffer(
            Status(HttpStatusCode.TooManyRequests, Error("RATE_LIMIT_EXCEEDED"), retryAfterSeconds: 1),
            Ok(Organizations()));
        Assert.Equal(PublishingProfileFetchOutcome.Fetched, (await recovers.Gateway().FetchAsync(Ct)).Outcome);
        Assert.Equal(2, recovers.Requests.Count);

        var keepsThrottling = new StubBuffer(
            Status(HttpStatusCode.TooManyRequests, retryAfterSeconds: 1),
            Status(HttpStatusCode.TooManyRequests, retryAfterSeconds: 1),
            Ok(Organizations()));
        var result = await keepsThrottling.Gateway().FetchAsync(Ct);

        Assert.Equal(PublishingProfileFetchOutcome.RateLimited, result.Outcome);
        Assert.Equal(2, keepsThrottling.Requests.Count);
    }

    [Fact]
    public async Task A_throttle_that_does_not_say_for_how_long_says_so_rather_than_guessing()
    {
        var result = await new StubBuffer(Ok(Error("RATE_LIMIT_EXCEEDED"))).Gateway().FetchAsync(Ct);

        Assert.Equal(PublishingProfileFetchOutcome.RateLimited, result.Outcome);
        Assert.Null(result.RetryAfter);
    }

    // ---- Transient failures ----

    [Fact]
    public async Task A_failure_that_may_pass_is_tried_three_times_and_no_more()
    {
        var recovers = new StubBuffer(
            Status(HttpStatusCode.BadGateway), Ok(Error("UNEXPECTED")), Ok(Organizations()));
        Assert.Equal(PublishingProfileFetchOutcome.Fetched, (await recovers.Gateway().FetchAsync(Ct)).Outcome);
        Assert.Equal(3, recovers.Requests.Count);

        var down = new StubBuffer(
            Status(HttpStatusCode.ServiceUnavailable),
            Status(HttpStatusCode.ServiceUnavailable),
            Status(HttpStatusCode.ServiceUnavailable),
            Ok(Organizations()));
        Assert.Equal(PublishingProfileFetchOutcome.Unavailable, (await down.Gateway().FetchAsync(Ct)).Outcome);
        Assert.Equal(3, down.Requests.Count);
    }

    [Fact]
    public async Task A_dropped_connection_and_an_answer_that_never_comes_are_both_unavailable()
    {
        var dropped = new StubBuffer(Scripted.Throws, Scripted.Throws, Scripted.Throws);
        Assert.Equal(PublishingProfileFetchOutcome.Unavailable, (await dropped.Gateway().FetchAsync(Ct)).Outcome);
        Assert.Equal(3, dropped.Requests.Count);

        var silent = new StubBuffer(Scripted.Hangs, Scripted.Hangs, Scripted.Hangs);
        Assert.Equal(PublishingProfileFetchOutcome.Unavailable, (await silent.Gateway().FetchAsync(Ct)).Outcome);
        Assert.Equal(3, silent.Requests.Count);
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_swallowed_as_a_provider_failure()
    {
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var buffer = new StubBuffer(Scripted.Hangs);
        var pending = buffer.Gateway(attemptTimeout: TimeSpan.FromSeconds(30)).FetchAsync(cancelled.Token);

        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    // ---- Answers that cannot be used ----

    [Theory]
    [InlineData("<html>Bad gateway</html>")]
    [InlineData("[]")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":{\"account\":{\"organizations\":\"nope\"}}}")]
    [InlineData("{\"data\":{\"account\":{\"organizations\":[{\"id\":7}]}}}")]
    [InlineData("{\"errors\":[{\"message\":\"x\",\"extensions\":{\"code\":\"SOMETHING_NEW\"}}]}")]
    [InlineData("{\"errors\":[{\"message\":\"x\"}]}")]
    [InlineData("{\"errors\":[{\"message\":\"x\",\"extensions\":{\"code\":\"NOT_FOUND\"}}]}")]
    public async Task An_answer_that_cannot_be_read_is_unavailable_and_is_not_asked_for_again(string body)
    {
        var buffer = new StubBuffer(Ok(body));

        var result = await buffer.Gateway().FetchAsync(Ct);

        Assert.Equal(PublishingProfileFetchOutcome.Unavailable, result.Outcome);
        Assert.Empty(result.Profiles);
        Assert.Single(buffer.Requests);
    }

    [Fact]
    public async Task A_response_too_large_to_be_a_profile_list_is_not_read()
    {
        var huge = "{\"data\":{\"account\":{\"organizations\":[],\"pad\":\"" + new string('x', 1024 * 1024 + 16) + "\"}}}";

        Assert.Equal(PublishingProfileFetchOutcome.Unavailable, (await new StubBuffer(Ok(huge)).Gateway().FetchAsync(Ct)).Outcome);
    }

    [Fact]
    public async Task One_organization_failing_fails_the_read_rather_than_returning_half_a_list()
    {
        // A partial list would be stored as "the other organization's profiles were removed".
        var buffer = new StubBuffer(
            Ok(Organizations(OrgOne, OrgTwo)),
            Ok(Channels(Channel("ch_EXTERNAL_1", "instagram"))),
            Ok(Error("FORBIDDEN")));

        var result = await buffer.Gateway().FetchAsync(Ct);

        Assert.Equal(PublishingProfileFetchOutcome.CredentialRejected, result.Outcome);
        Assert.Empty(result.Profiles);
    }

    [Fact]
    public async Task A_channel_with_no_id_cannot_be_mapped_and_fails_the_read()
    {
        var buffer = new StubBuffer(
            Ok(Organizations(OrgOne)),
            Ok("{\"data\":{\"channels\":[{\"name\":\"cozyfall\",\"service\":\"instagram\"}]}}"));

        Assert.Equal(PublishingProfileFetchOutcome.Unavailable, (await buffer.Gateway().FetchAsync(Ct)).Outcome);
    }

    [Fact]
    public async Task One_read_is_bounded_however_many_organizations_and_channels_the_provider_lists()
    {
        var organizations = Enumerable.Range(0, 25).Select(index => $"org_{index}").ToArray();
        var perOrganization = Enumerable.Range(0, 60).Select(index => Channel($"ch_{Guid.NewGuid():N}", "instagram")).ToArray();
        var buffer = new StubBuffer([Ok(Organizations(organizations)), .. Enumerable.Repeat(Ok(Channels(perOrganization)), 25)]);

        var result = await buffer.Gateway().FetchAsync(Ct);

        Assert.Equal(PublishingProfileFetchOutcome.Fetched, result.Outcome);
        Assert.Equal(PublishingPolicy.MaxProfilesPerRead, result.Profiles.Count);
        Assert.True(buffer.Requests.Count <= 1 + PublishingPolicy.MaxProviderAccounts);

        // And it says it was cut short, so nothing is taken to be gone on the strength of it.
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task A_read_says_it_was_cut_short_exactly_when_it_was()
    {
        var whole = await new StubBuffer(Ok(Organizations(OrgOne)), Ok(Channels(Channel("ch_EXTERNAL_1", "instagram")))).Gateway().FetchAsync(Ct);
        Assert.False(whole.Truncated);

        // More organizations than one read takes, each with a single channel.
        var many = Enumerable.Range(0, PublishingPolicy.MaxProviderAccounts + 1).Select(index => $"org_{index}").ToArray();
        var buffer = new StubBuffer(
            [Ok(Organizations(many)), .. many.Take(PublishingPolicy.MaxProviderAccounts).Select(_ => Ok(Channels(Channel($"ch_{Guid.NewGuid():N}", "instagram"))))]);
        var cut = await buffer.Gateway().FetchAsync(Ct);

        Assert.Equal(PublishingProfileFetchOutcome.Fetched, cut.Outcome);
        Assert.Equal(PublishingPolicy.MaxProviderAccounts, cut.Profiles.Count);
        Assert.True(cut.Truncated);

        // Exactly at the cap, with nothing left unread, is a whole read.
        var exact = Enumerable.Range(0, PublishingPolicy.MaxProfilesPerRead).Select(index => Channel($"ch_{index}", "instagram")).ToArray();
        var full = await new StubBuffer(Ok(Organizations(OrgOne)), Ok(Channels(exact))).Gateway().FetchAsync(Ct);
        Assert.Equal(PublishingPolicy.MaxProfilesPerRead, full.Profiles.Count);
        Assert.False(full.Truncated);
    }

    // ---- What never leaves ----

    [Fact]
    public async Task Nothing_of_the_providers_and_nothing_secret_is_ever_logged()
    {
        using var logs = new CapturingLoggerProvider();

        // A good read, a refusal, a throttle and an outage, each through the same logger.
        foreach (var script in new[]
                 {
                     new[] { Ok(Organizations(OrgOne)), Ok(Channels(Channel("ch_EXTERNAL_1", "instagram"))) },
                     [Ok(Error("UNAUTHORIZED"))],
                     [Status(HttpStatusCode.TooManyRequests, Error("RATE_LIMIT_EXCEEDED"), retryAfterSeconds: 900)],
                     [Status(HttpStatusCode.BadGateway, ProviderSays), Scripted.Throws, Ok(ProviderSays)],
                 })
        {
            await new StubBuffer(script).Gateway(logs).FetchAsync(Ct);
        }

        Assert.NotEmpty(logs.Entries);
        var everything = string.Join('\n', logs.Entries);

        Assert.DoesNotContain(ApiKey, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("EXTERNAL", everything, StringComparison.Ordinal);
        Assert.DoesNotContain(ProviderSays, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("Cozy Fall", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("api.buffer.example", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", everything, StringComparison.OrdinalIgnoreCase);

        // What is there: how each read ended.
        Assert.Contains("Fetched", everything, StringComparison.Ordinal);
        Assert.Contains("CredentialRejected", everything, StringComparison.Ordinal);
        Assert.Contains("RateLimited", everything, StringComparison.Ordinal);
    }

    [Fact]
    public void It_describes_itself_by_provider_and_workspace_and_never_by_its_key()
    {
        var described = new StubBuffer().Gateway().Describe();

        Assert.Equal(new PublishingProviderDescriptor(true, "buffer", Workspace), described);
        Assert.DoesNotContain(ApiKey, described.ToString(), StringComparison.Ordinal);
    }

    // ---- Configuration ----

    [Fact]
    public void A_complete_connection_string_is_read_and_its_printed_form_leaves_out_the_key()
    {
        var options = BufferPublishingOptions.Parse($" Endpoint=https://api.buffer.com ; Key={ApiKey};Workspace={Workspace} ");

        Assert.NotNull(options);
        Assert.Equal(new Uri("https://api.buffer.com"), options.Endpoint);
        Assert.Equal(ApiKey, options.ApiKey);
        Assert.Equal(Workspace, options.WorkspaceId);
        Assert.DoesNotContain(ApiKey, options.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Endpoint=https://api.buffer.com;Key=abc")]
    [InlineData("Endpoint=https://api.buffer.com;Workspace=7d0c1f0e-52a4-4c1b-9a35-3d5b6c1f0a11")]
    [InlineData("Key=abc;Workspace=7d0c1f0e-52a4-4c1b-9a35-3d5b6c1f0a11")]
    [InlineData("Endpoint=https://api.buffer.com;Key=;Workspace=7d0c1f0e-52a4-4c1b-9a35-3d5b6c1f0a11")]
    [InlineData("Endpoint=not a url;Key=abc;Workspace=7d0c1f0e-52a4-4c1b-9a35-3d5b6c1f0a11")]
    [InlineData("Endpoint=http://api.buffer.com;Key=abc;Workspace=7d0c1f0e-52a4-4c1b-9a35-3d5b6c1f0a11")]

    // A workspace named by its slug is not named at all: a slug can be claimed, so a key bound to one is
    // bound to nobody, and the provider stays off.
    [InlineData("Endpoint=https://api.buffer.com;Key=abc;Workspace=cozy-fall")]
    [InlineData("Endpoint=https://api.buffer.com;Key=abc;Workspace=00000000-0000-0000-0000-000000000000")]
    public void A_missing_or_half_made_connection_string_is_no_provider_rather_than_a_fault(string? connectionString) =>
        Assert.Null(BufferPublishingOptions.Parse(connectionString));

    [Fact]
    public async Task A_host_with_no_provider_says_so_and_calls_nothing()
    {
        var gateway = new UnconfiguredPublishingProfileGateway();

        Assert.Equal(PublishingProviderDescriptor.None, gateway.Describe());
        Assert.Equal(PublishingProfileFetchOutcome.NotConfigured, (await gateway.FetchAsync(Ct)).Outcome);
    }

    // ---- The stub ----

    private sealed record Scripted(HttpStatusCode Status, string Body, int? RetryAfterSeconds = null, bool Fails = false, bool Never = false)
    {
        /// <summary>The connection drops. Its message names the address, which must not be logged.</summary>
        public static readonly Scripted Throws = new(0, string.Empty, Fails: true);

        /// <summary>No answer until the attempt gives up.</summary>
        public static readonly Scripted Hangs = new(0, string.Empty, Never: true);
    }

    private sealed record Sent(HttpMethod Method, string Uri, string Raw, JsonElement Body);

    /// <summary>Buffer, as a queue of recorded responses.</summary>
    private sealed class StubBuffer(params Scripted[] script) : HttpMessageHandler
    {
        private readonly Queue<Scripted> _script = new(script);

        public List<Sent> Requests { get; } = [];

        public BufferPublishingProfileGateway Gateway(CapturingLoggerProvider? logs = null, TimeSpan? attemptTimeout = null)
        {
            var http = new HttpClient(this);
            http.DefaultRequestHeaders.Authorization = new("Bearer", ApiKey);

            return new BufferPublishingProfileGateway(
                http,
                new BufferPublishingOptions(new Uri("https://api.buffer.example"), ApiKey, Workspace)
                {
                    AttemptTimeout = attemptTimeout ?? TimeSpan.FromMilliseconds(150),
                    RetryBaseDelay = TimeSpan.FromMilliseconds(1),
                },
                (logs is null ? LoggerFactory.Create(_ => { }) : LoggerFactory.Create(builder => builder.AddProvider(logs)))
                    .CreateLogger<BufferPublishingProfileGateway>());
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var raw = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Sent(request.Method, request.RequestUri!.ToString(), raw, JsonDocument.Parse(raw).RootElement.Clone()));

            var next = _script.Count > 0 ? _script.Dequeue() : throw new InvalidOperationException("The gateway asked for more than was scripted.");

            if (next.Fails)
            {
                throw new HttpRequestException("No such host is known. (api.buffer.example:443)");
            }

            if (next.Never)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            var response = new HttpResponseMessage(next.Status) { Content = new StringContent(next.Body, Encoding.UTF8, "application/json") };
            if (next.RetryAfterSeconds is { } seconds)
            {
                response.Headers.RetryAfter = new(TimeSpan.FromSeconds(seconds));
            }

            return response;
        }
    }
}
