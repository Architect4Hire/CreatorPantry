using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Modules.Publishing.Gateways;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Publishing.Integration.Buffer;

/// <summary>
/// What a host needs to talk to Buffer, read out of one connection string.
/// </summary>
/// <param name="Endpoint">Buffer's GraphQL endpoint.</param>
/// <param name="ApiKey">A Buffer API key. A credential: never logged, never returned, never put in an exception.</param>
/// <param name="WorkspaceId">The one workspace whose Buffer account the key is, by id. See <c>PublishingProviderDescriptor</c>.</param>
public sealed record BufferPublishingOptions(Uri Endpoint, string ApiKey, Guid WorkspaceId)
{
    /// <summary>The connection string's name, and the named <c>HttpClient</c>'s.</summary>
    public const string ConnectionName = "publishing-buffer";

    /// <summary>How long one attempt may take before it is abandoned.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The first wait between attempts; the second is twice it.</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Printed nowhere on purpose, but if it ever is, it is not with the key in it.</summary>
    public override string ToString() => $"BufferPublishingOptions {{ Endpoint = {Endpoint}, WorkspaceId = {WorkspaceId} }}";

    /// <summary>
    /// Reads <c>Endpoint=…;Key=…;Workspace=&lt;workspace id&gt;</c>. Null when the value is absent or any part is
    /// missing or unusable — a workspace given as anything but an id included: a half-configured provider is an
    /// unconfigured one, never a host that will not start, and never a key with nobody it is known to belong to.
    /// </summary>
    public static BufferPublishingOptions? Parse(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = part.IndexOf('=', StringComparison.Ordinal);
            if (at > 0)
            {
                parts[part[..at].Trim()] = part[(at + 1)..].Trim();
            }
        }

        return parts.TryGetValue("Endpoint", out var endpoint)
            && parts.TryGetValue("Key", out var key)
            && parts.TryGetValue("Workspace", out var workspace)
            && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && key.Length > 0
            && Guid.TryParse(workspace, out var workspaceId)
            && workspaceId != Guid.Empty
                ? new BufferPublishingOptions(uri, key, workspaceId)
                : null;
    }
}

/// <summary>
/// Buffer, as a publishing provider's list of profiles (INT-030).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The contract this is written against</strong> is Buffer's GraphQL API as relaunched on 27 May 2026
/// (developers.buffer.com): one endpoint, <c>POST https://api.buffer.com</c>, a personal API key as a bearer
/// token, and what the product calls a profile Buffer calls a <em>channel</em>, listed per organization. Two
/// read-only queries: the account's organizations, then each one's channels.
/// </para>
/// <para>
/// <strong>HTTP 200 is not success.</strong> Buffer answers every failure but throttling with 200 and an
/// <c>errors</c> array; what happened is in <c>extensions.code</c>. Throttling is a 429 with
/// <c>Retry-After</c>. Both are read here and nowhere else.
/// </para>
/// <para>
/// <strong>Retries are this gateway's own, and bounded.</strong> Both queries are read-only, so a transient
/// failure — a 5xx, <c>UNEXPECTED</c>, a timeout, a dropped connection — is tried up to three times with a
/// short backoff. A throttle is waited out once and only when Buffer asks for two seconds or less; anything
/// longer is reported, because holding a request open for a quota window helps nobody. A refused credential is
/// never retried. The named client carries no resilience handler of its own: the host removes the default one,
/// which would retry every POST and so, later, a mutation.
/// </para>
/// <para>
/// <strong>Nothing of Buffer's is logged but a shape.</strong> The outcome, the HTTP status, the error code
/// Buffer documented and the elapsed time. Never the key, a response body, an id or a message: a provider's
/// error text can quote the request back, and its ids are for the mapping record alone (external.md).
/// </para>
/// <para>
/// <strong>Only the fields that are mapped are asked for.</strong> No avatar — it is an address Buffer chose,
/// and this server neither fetches nor relays one — and nothing that would cost query budget for no use.
/// </para>
/// </remarks>
public sealed class BufferPublishingProfileGateway(
    HttpClient http,
    BufferPublishingOptions options,
    ILogger<BufferPublishingProfileGateway> logger) : IPublishingProfileGateway
{
    private const int MaxAttempts = 3;
    private const int MaxResponseBytes = 1024 * 1024;
    private static readonly TimeSpan LongestThrottleWorthWaitingFor = TimeSpan.FromSeconds(2);

    private const string OrganizationsQuery = "query { account { organizations { id } } }";

    private const string ChannelsQuery =
        "query($input: ChannelsInput!) { channels(input: $input) "
        + "{ id name displayName service timezone isDisconnected isLocked organizationId } }";

    public PublishingProviderDescriptor Describe() =>
        new(true, PublishingPolicy.BufferProviderKey, options.WorkspaceId);

    public async Task<PublishingProfileFetch> FetchAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();

        var result = await FetchCoreAsync(cancellationToken);

        // The whole of what is recorded about a read: how it ended, how many profiles, how long.
        logger.LogInformation(
            "Buffer profile read ended {Outcome} with {ProfileCount} profiles in {ElapsedMs} ms.",
            result.Outcome,
            result.Profiles.Count,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        return result;
    }

    private async Task<PublishingProfileFetch> FetchCoreAsync(CancellationToken cancellationToken)
    {
        var organizations = await SendAsync(OrganizationsQuery, variables: null, cancellationToken);
        if (organizations.Failure is { } failed)
        {
            return failed;
        }

        if (!TryReadOrganizationIds(organizations.Data, out var organizationIds))
        {
            return PublishingProfileFetch.Of(PublishingProfileFetchOutcome.Unavailable);
        }

        var profiles = new List<ProviderPublishingProfile>();

        // Cut short by either cap, the read says so: the profiles it never reached are not known to be gone.
        var truncated = organizationIds.Count > PublishingPolicy.MaxProviderAccounts;
        var read = 0;

        foreach (var organizationId in organizationIds.Take(PublishingPolicy.MaxProviderAccounts))
        {
            // The id travels as a variable, never spliced into the query text.
            var channels = await SendAsync(
                ChannelsQuery, new { input = new { organizationId } }, cancellationToken);
            if (channels.Failure is { } channelsFailed)
            {
                // A partial list would be read as "the rest were removed". All of it, or none.
                return channelsFailed;
            }

            if (!TryReadChannels(channels.Data, organizationId, profiles))
            {
                return PublishingProfileFetch.Of(PublishingProfileFetchOutcome.Unavailable);
            }

            read++;

            if (profiles.Count >= PublishingPolicy.MaxProfilesPerRead)
            {
                truncated |= profiles.Count > PublishingPolicy.MaxProfilesPerRead
                    || read < Math.Min(organizationIds.Count, PublishingPolicy.MaxProviderAccounts);
                break;
            }
        }

        return new PublishingProfileFetch(
            PublishingProfileFetchOutcome.Fetched,
            [.. profiles.Take(PublishingPolicy.MaxProfilesPerRead)],
            Truncated: truncated);
    }

    /// <summary>One GraphQL request, retried while that is safe and worthwhile.</summary>
    private async Task<(JsonElement Data, PublishingProfileFetch? Failure)> SendAsync(
        string query, object? variables, CancellationToken cancellationToken)
    {
        var waitedForThrottle = false;

        for (var attempt = 1; ; attempt++)
        {
            var reply = await SendOnceAsync(query, variables, cancellationToken);

            if (reply.Kind is ReplyKind.Data)
            {
                return (reply.Data, null);
            }

            if (reply.Kind is ReplyKind.Throttled)
            {
                if (!waitedForThrottle && reply.RetryAfter is { } wait && wait <= LongestThrottleWorthWaitingFor)
                {
                    waitedForThrottle = true;
                    await Task.Delay(wait, cancellationToken);
                    continue;
                }

                return (default, PublishingProfileFetch.Of(PublishingProfileFetchOutcome.RateLimited, reply.RetryAfter));
            }

            if (reply.Kind is ReplyKind.Transient && attempt < MaxAttempts)
            {
                await Task.Delay(options.RetryBaseDelay * attempt, cancellationToken);
                continue;
            }

            return (default, PublishingProfileFetch.Of(reply.Kind is ReplyKind.Refused
                ? PublishingProfileFetchOutcome.CredentialRejected
                : PublishingProfileFetchOutcome.Unavailable));
        }
    }

    private async Task<Reply> SendOnceAsync(string query, object? variables, CancellationToken cancellationToken)
    {
        using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptTimeout.CancelAfter(options.AttemptTimeout);

        try
        {
            // The key is on the named client's headers, set by the host; nothing here handles it.
            using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
            {
                Content = JsonContent.Create(new { query, variables }),
            };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptTimeout.Token);

            if (response.StatusCode is HttpStatusCode.TooManyRequests)
            {
                return Reply.Throttled(RetryAfterOf(response));
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Note(Reply.Refused, (int)response.StatusCode, code: null);
            }

            if ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout)
            {
                return Note(Reply.Transient, (int)response.StatusCode, code: null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Note(Reply.Unusable, (int)response.StatusCode, code: null);
            }

            using var document = await ReadJsonAsync(response, attemptTimeout.Token);
            if (document is null || document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return Note(Reply.Unusable, (int)response.StatusCode, code: "unreadable_body");
            }

            var root = document.RootElement;

            // 200 with an errors array is how Buffer reports everything but a throttle.
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind is JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                var code = FirstErrorCode(errors);

                return code switch
                {
                    "UNAUTHORIZED" or "FORBIDDEN" => Note(Reply.Refused, 200, code),
                    "RATE_LIMIT_EXCEEDED" => Reply.Throttled(RetryAfterOf(response)),
                    "UNEXPECTED" => Note(Reply.Transient, 200, code),

                    // NOT_FOUND, or a code this was not written against: not something another attempt changes.
                    _ => Note(Reply.Unusable, 200, code is null ? "unknown" : "unrecognised"),
                };
            }

            return root.TryGetProperty("data", out var data) && data.ValueKind is JsonValueKind.Object
                ? Reply.Of(data.Clone())
                : Note(Reply.Unusable, 200, "no_data");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The attempt's own clock, not the caller's: Buffer did not answer in time.
            return Note(Reply.Transient, status: 0, code: "timeout");
        }
        catch (HttpRequestException)
        {
            // By type alone. The message can carry the address, and nothing here needs it.
            return Note(Reply.Transient, status: 0, code: "connection");
        }
        catch (JsonException)
        {
            return Note(Reply.Unusable, status: 200, code: "unreadable_body");
        }
    }

    /// <summary>Records the shape of a failed attempt and hands the reply back. No body, id, message or key.</summary>
    private Reply Note(Reply reply, int status, string? code)
    {
        logger.LogWarning(
            "Buffer request attempt ended {ReplyKind} (HTTP {Status}, code {Code}).", reply.Kind, status, code ?? "none");

        return reply;
    }

    private static async Task<JsonDocument?> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Bounded: a response is a stranger's bytes, and no profile list is anywhere near this size.
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
    }

    /// <summary>The first error's documented code, and only if it is one: an arbitrary string is not passed on.</summary>
    private static string? FirstErrorCode(JsonElement errors)
    {
        foreach (var error in errors.EnumerateArray())
        {
            if (error.ValueKind is JsonValueKind.Object
                && error.TryGetProperty("extensions", out var extensions)
                && extensions.ValueKind is JsonValueKind.Object
                && extensions.TryGetProperty("code", out var code)
                && code.ValueKind is JsonValueKind.String)
            {
                return code.GetString() switch
                {
                    "UNAUTHORIZED" => "UNAUTHORIZED",
                    "FORBIDDEN" => "FORBIDDEN",
                    "NOT_FOUND" => "NOT_FOUND",
                    "UNEXPECTED" => "UNEXPECTED",
                    "RATE_LIMIT_EXCEEDED" => "RATE_LIMIT_EXCEEDED",
                    _ => null,
                };
            }
        }

        return null;
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        // Buffer documents seconds. A date would need a clock to read, and a wait it cannot state is treated
        // as unstated: the caller then falls back to its own.
        return response.Headers.RetryAfter?.Delta is { } wait && wait > TimeSpan.Zero ? wait : null;
    }

    private static bool TryReadOrganizationIds(JsonElement data, out List<string> ids)
    {
        ids = [];

        if (!data.TryGetProperty("account", out var account)
            || account.ValueKind is not JsonValueKind.Object
            || !account.TryGetProperty("organizations", out var organizations)
            || organizations.ValueKind is not JsonValueKind.Array)
        {
            return false;
        }

        foreach (var organization in organizations.EnumerateArray())
        {
            if (Text(organization, "id") is not { } id)
            {
                return false;
            }

            ids.Add(id);
        }

        return true;
    }

    private static bool TryReadChannels(JsonElement data, string organizationId, List<ProviderPublishingProfile> into)
    {
        if (!data.TryGetProperty("channels", out var channels) || channels.ValueKind is not JsonValueKind.Array)
        {
            return false;
        }

        foreach (var channel in channels.EnumerateArray())
        {
            // A channel with no id cannot be mapped to anything, and one that is too long is not one of Buffer's.
            if (Text(channel, "id") is not { Length: <= PublishingPolicy.ExternalIdMaxLength } id
                || organizationId.Length > PublishingPolicy.ExternalIdMaxLength)
            {
                return false;
            }

            into.Add(new ProviderPublishingProfile(
                id,
                organizationId,
                PlatformOf(Text(channel, "service")),
                Text(channel, "displayName") ?? Text(channel, "name") ?? string.Empty,
                Text(channel, "timezone"),
                Flag(channel, "isDisconnected") ? PublishingProfileStatus.NeedsReconnecting
                    : Flag(channel, "isLocked") ? PublishingProfileStatus.Locked
                    : PublishingProfileStatus.Ready));
        }

        return true;
    }

    /// <summary>Buffer's name for a network, as CreatorPantry's. One it does not know is Other, never a failure.</summary>
    internal static PublishingPlatform PlatformOf(string? service) => service?.ToLowerInvariant() switch
    {
        "instagram" => PublishingPlatform.Instagram,
        "facebook" => PublishingPlatform.Facebook,
        "twitter" => PublishingPlatform.X,
        "linkedin" => PublishingPlatform.LinkedIn,
        "pinterest" => PublishingPlatform.Pinterest,
        "tiktok" => PublishingPlatform.TikTok,
        "youtube" => PublishingPlatform.YouTube,
        "mastodon" => PublishingPlatform.Mastodon,
        "bluesky" => PublishingPlatform.Bluesky,
        "threads" => PublishingPlatform.Threads,
        "whatsapp" => PublishingPlatform.WhatsApp,
        _ => PublishingPlatform.Other,
    };

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind is JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.String
        && value.GetString() is { } text
        && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True;

    private enum ReplyKind
    {
        Data,
        Throttled,
        Transient,
        Refused,
        Unusable,
    }

    private readonly record struct Reply(ReplyKind Kind, JsonElement Data = default, TimeSpan? RetryAfter = null)
    {
        public static readonly Reply Transient = new(ReplyKind.Transient);
        public static readonly Reply Refused = new(ReplyKind.Refused);
        public static readonly Reply Unusable = new(ReplyKind.Unusable);

        public static Reply Of(JsonElement data) => new(ReplyKind.Data, data);

        public static Reply Throttled(TimeSpan? retryAfter) => new(ReplyKind.Throttled, RetryAfter: retryAfter);
    }
}
