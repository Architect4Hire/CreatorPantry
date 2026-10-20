using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ingredients.Gateways;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Ingredients.Integration.UsdaFdc;

/// <summary>
/// USDA FoodData Central, as the approved source of nutrition and density evidence (14.1, B-09, B-33).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The contract this is written against</strong> is FDC's REST API v1 (fdc.nal.usda.gov/api-guide,
/// read 2026-10-20): <c>GET food/{fdcId}</c> and <c>GET foods/search</c> under
/// <c>https://api.nal.usda.gov/fdc/v1/</c>, an api.data.gov key, and 1,000 requests an hour <em>per IP
/// address</em>; going over blocks the <em>key</em> for an hour, and every request answers 429. The data is
/// public domain under CC0 1.0. What the responses actually contain is in <see cref="UsdaFdcFoodReader"/>.
/// </para>
/// <para>
/// <strong>The quota is shared with whatever else leaves the same address</strong>, which this process cannot
/// see. <see cref="UsdaFdcThrottle"/> stops this process adding to a block it has been told about; it cannot
/// prevent one another client caused.
/// </para>
/// <para>
/// <strong>The key travels in the <c>X-Api-Key</c> header, never the URL.</strong> The guide's samples use the
/// <c>api_key</c> query parameter; the header is api.data.gov's alternative, and the recorded 200s in the test
/// fixtures were made with it. A URL is what request logging, traces and proxies record.
/// </para>
/// <para>
/// <strong>Retries are this gateway's own, and bounded.</strong> Every call is a read, so a transient failure — a
/// 5xx, a timeout, a dropped connection — is tried up to three times with a short backoff. A throttle is never
/// retried: it blocks the whole process through <see cref="UsdaFdcThrottle"/> until the wait is over, because a
/// request sent into a block is refused and counted. A refused key is never retried. The named client carries
/// no resilience handler of its own: the standard one retries 429s, which is the one thing this source punishes.
/// </para>
/// <para>
/// <strong>Nothing of FDC's is logged here but a shape.</strong> The operation, outcome, HTTP status and elapsed
/// time; never the key, a response body or a provider message. The host's HTTP client logging and tracing do
/// record request URLs, and a search's URL holds its term — which is why a term can only ever be a platform
/// ingredient's name (<see cref="ReferenceFoodSearch"/>), never a creator's text.
/// </para>
/// </remarks>
public sealed class UsdaFdcEvidenceGateway(
    HttpClient http,
    UsdaFdcEvidenceOptions options,
    UsdaFdcThrottle throttle,
    IClock clock,
    ILogger<UsdaFdcEvidenceGateway> logger) : IReferenceEvidenceGateway
{
    private const int MaxAttempts = 3;

    /// <summary>A stranger's bytes, bounded. The largest recorded food is under 200 KB.</summary>
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    private static ApprovedEvidenceSource Source => EvidenceSourceCatalog.UsdaFoodDataCentral;

    public EvidenceCoverage Describe(ReferenceEvidenceKind kind) => EvidenceCoverage.Of(kind, [Source.Code]);

    public async Task<ReferenceFoodFetch> GetFoodAsync(string externalRecordId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(externalRecordId);

        // An FDC id is digits. Anything else is not one of its records, and is never spliced into a path.
        if (externalRecordId.Length is 0 or > EvidencePolicy.ExternalRecordIdMaxLength
            || !externalRecordId.All(char.IsAsciiDigit))
        {
            return ReferenceFoodFetch.Of(ReferenceEvidenceOutcome.NotFound);
        }

        var started = Stopwatch.GetTimestamp();
        var reply = await SendAsync($"food/{externalRecordId}", cancellationToken);

        var result = reply.Kind switch
        {
            ReplyKind.Data => UsdaFdcFoodReader.ReadFood(reply.Data, Source, clock.UtcNow, out var food) switch
            {
                UsdaFdcReadResult.Read => new ReferenceFoodFetch(ReferenceEvidenceOutcome.Found, food),
                UsdaFdcReadResult.NotApproved => ReferenceFoodFetch.Of(ReferenceEvidenceOutcome.NoApprovedSource),
                _ => ReferenceFoodFetch.Of(ReferenceEvidenceOutcome.Unreadable),
            },
            _ => ReferenceFoodFetch.Of(OutcomeOf(reply.Kind), reply.RetryAfter),
        };

        Record("food", result.Outcome, reply.Status, started);
        return result;
    }

    public async Task<ReferenceFoodSearchResult> SearchFoodsAsync(ReferenceFoodSearch search, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);

        var dataTypes = string.Concat(UsdaFdcFoodReader.ApprovedDataTypes.Select(type => $"&dataType={Uri.EscapeDataString(type)}"));
        var path = $"foods/search?query={Uri.EscapeDataString(search.Term)}{dataTypes}"
            + $"&pageSize={EvidencePolicy.MaxSearchResults.ToString(CultureInfo.InvariantCulture)}";

        var started = Stopwatch.GetTimestamp();
        var reply = await SendAsync(path, cancellationToken);

        ReferenceFoodSearchResult result;
        if (reply.Kind is ReplyKind.Data)
        {
            var candidates = new List<ReferenceFoodCandidate>();
            result = UsdaFdcFoodReader.TryReadSearch(reply.Data, Source, candidates)
                ? new ReferenceFoodSearchResult(ReferenceEvidenceOutcome.Found, candidates)
                : ReferenceFoodSearchResult.Of(ReferenceEvidenceOutcome.Unreadable);
        }
        else
        {
            result = ReferenceFoodSearchResult.Of(OutcomeOf(reply.Kind), reply.RetryAfter);
        }

        Record("search", result.Outcome, reply.Status, started);
        return result;
    }

    private static ReferenceEvidenceOutcome OutcomeOf(ReplyKind kind) => kind switch
    {
        ReplyKind.NotFound => ReferenceEvidenceOutcome.NotFound,
        ReplyKind.Throttled => ReferenceEvidenceOutcome.Throttled,
        ReplyKind.Refused => ReferenceEvidenceOutcome.CredentialRejected,
        ReplyKind.Transient => ReferenceEvidenceOutcome.Unavailable,
        _ => ReferenceEvidenceOutcome.Unreadable,
    };

    /// <summary>The whole of what is recorded about a read.</summary>
    private void Record(string operation, ReferenceEvidenceOutcome outcome, int status, long started) =>
        logger.LogInformation(
            "USDA FDC {Operation} read ended {Outcome} (HTTP {Status}) in {ElapsedMs} ms.",
            operation,
            outcome,
            status,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

    /// <summary>One read, retried while that is worthwhile, and not sent at all while the key is blocked.</summary>
    private async Task<Reply> SendAsync(string relativePath, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            // Before every attempt, not just the first: another request can be throttled while this one waits
            // out a retry, and an attempt sent into that block is refused and counted against it.
            if (throttle.Remaining() is { } left)
            {
                return new Reply(ReplyKind.Throttled, RetryAfter: left);
            }

            var reply = await SendOnceAsync(new Uri(options.Endpoint, relativePath), cancellationToken);

            if (reply.Kind is ReplyKind.Throttled)
            {
                var wait = reply.RetryAfter ?? options.DefaultThrottle;
                throttle.Block(wait);
                return reply with { RetryAfter = wait };
            }

            if (reply.Kind is ReplyKind.Transient && attempt < MaxAttempts)
            {
                await Task.Delay(options.RetryBaseDelay * attempt, cancellationToken);
                continue;
            }

            return reply;
        }
    }

    private async Task<Reply> SendOnceAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptTimeout.CancelAfter(options.AttemptTimeout);

        try
        {
            // The key is on the named client's headers, set by the host; nothing here handles it.
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptTimeout.Token);
            var status = (int)response.StatusCode;

            switch (status)
            {
                case (int)HttpStatusCode.TooManyRequests:
                    return new Reply(ReplyKind.Throttled, Status: status, RetryAfter: RetryAfterOf(response));
                case (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden:
                    return Note(ReplyKind.Refused, status);
                case (int)HttpStatusCode.NotFound:
                    return new Reply(ReplyKind.NotFound, Status: status);
                case (int)HttpStatusCode.RequestTimeout or >= 500:
                    return Note(ReplyKind.Transient, status);
                case < 200 or >= 300:
                    return Note(ReplyKind.Unusable, status);
            }

            using var document = await ReadJsonAsync(response, attemptTimeout.Token);
            return document is null
                ? Note(ReplyKind.Unusable, status)
                : new Reply(ReplyKind.Data, document.RootElement.Clone(), Status: status);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The attempt's own clock, not the caller's: FDC did not answer in time.
            return Note(ReplyKind.Transient, status: 0);
        }
        catch (HttpRequestException)
        {
            // By type alone. The message can carry the address, and nothing here needs it.
            return Note(ReplyKind.Transient, status: 0);
        }
        catch (IOException)
        {
            return Note(ReplyKind.Transient, status: 0);
        }
        catch (JsonException)
        {
            return Note(ReplyKind.Unusable, status: 200);
        }
    }

    private Reply Note(ReplyKind kind, int status)
    {
        logger.LogWarning("USDA FDC request attempt ended {ReplyKind} (HTTP {Status}).", kind, status);
        return new Reply(kind, Status: status);
    }

    private static async Task<JsonDocument?> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
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

    /// <summary>A wait in seconds, when the source states one. A date would need a clock to read, and is treated as unstated.</summary>
    private static TimeSpan? RetryAfterOf(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta is { } wait && wait > TimeSpan.Zero ? wait : null;

    private enum ReplyKind
    {
        Data,
        NotFound,
        Throttled,
        Refused,
        Transient,
        Unusable,
    }

    private readonly record struct Reply(ReplyKind Kind, JsonElement Data = default, int Status = 0, TimeSpan? RetryAfter = null);
}
