using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Publishing.Integration.Buffer;

/// <summary>How one request to Buffer ended, before any gateway decides what to do about it.</summary>
internal enum BufferReplyKind
{
    /// <summary>Buffer answered with a <c>data</c> object. What is in it is the caller's to read.</summary>
    Data,

    /// <summary>Throttled. Safe to repeat, and only after the wait.</summary>
    Throttled,

    /// <summary>Failed in a way another attempt could pass — but only for an operation that is safe to repeat.</summary>
    Transient,

    /// <summary>The credential was refused, or may not do this. Never worth repeating.</summary>
    Refused,

    /// <summary>Answered with something this contract has no word for. Not worth repeating either.</summary>
    Unusable,
}

/// <param name="Status">Buffer's HTTP status, or 0 when nothing was received.</param>
/// <param name="Code">
/// The documented error code, or a short word for what was wrong with the answer. A category, never a message:
/// a provider's error text can quote the request back (external.md).
/// </param>
internal readonly record struct BufferReply(
    BufferReplyKind Kind,
    JsonElement Data = default,
    TimeSpan? RetryAfter = null,
    int Status = 0,
    string? Code = null)
{
    public static BufferReply Of(JsonElement data) => new(BufferReplyKind.Data, data);

    public static BufferReply Throttled(TimeSpan? retryAfter, int status, JsonElement data = default) =>
        new(BufferReplyKind.Throttled, data, retryAfter, status, "RATE_LIMIT_EXCEEDED");

    /// <summary>The same reply with the <c>data</c> object that came beside the errors, when one did.</summary>
    public BufferReply With(JsonElement data) => this with { Data = data };
}

/// <summary>
/// One request to Buffer's GraphQL endpoint, classified. Shared by every Buffer gateway so that the same
/// answer is never read two ways.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It sends once per call and never retries of its own accord.</strong> Whether repeating is safe
/// depends on what was asked — a profile read is, a <c>createPost</c> is not — and that is the caller's to
/// know, so the decision is not taken here (INT-031).
/// </para>
/// <para>
/// <strong>HTTP 200 is not success.</strong> Buffer answers every failure but throttling with 200 and an
/// <c>errors</c> array, with what happened in <c>extensions.code</c>; throttling is a 429 with
/// <c>Retry-After</c> in seconds. Both are read here and nowhere else.
/// </para>
/// <para>
/// <strong>Nothing of Buffer's is logged but a shape.</strong> The classification, the HTTP status and the
/// documented code. Never the key, a body, an id or a message.
/// </para>
/// </remarks>
internal sealed class BufferGraphQl(HttpClient http, BufferPublishingOptions options, ILogger logger)
{
    private const int MaxResponseBytes = 1024 * 1024;

    public async Task<BufferReply> SendOnceAsync(string query, object? variables, CancellationToken cancellationToken)
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
                return BufferReply.Throttled(RetryAfterOf(response), (int)response.StatusCode);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Note(BufferReplyKind.Refused, (int)response.StatusCode, code: null);
            }

            if ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout)
            {
                return Note(BufferReplyKind.Transient, (int)response.StatusCode, code: null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Note(BufferReplyKind.Unusable, (int)response.StatusCode, code: null);
            }

            using var document = await ReadJsonAsync(response, attemptTimeout.Token);
            if (document is null || document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return Note(BufferReplyKind.Unusable, (int)response.StatusCode, code: "unreadable_body");
            }

            var root = document.RootElement;

            // 200 with an errors array is how Buffer reports everything but a throttle.
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind is JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                var code = FirstErrorCode(errors);

                // Buffer's error handling guide says even a successful mutation can answer with errors beside
                // its data, so the data travels with the classification. A caller whose request creates
                // something reads its own result first and the complaints second; without this, a post that
                // was made and warned about would be read as a post that was not made.
                var beside = root.TryGetProperty("data", out var alongside) && alongside.ValueKind is JsonValueKind.Object
                    ? alongside.Clone()
                    : default;

                return code switch
                {
                    "UNAUTHORIZED" or "FORBIDDEN" => Note(BufferReplyKind.Refused, 200, code).With(beside),
                    "RATE_LIMIT_EXCEEDED" => BufferReply.Throttled(RetryAfterOf(response), 200, beside),
                    "UNEXPECTED" => Note(BufferReplyKind.Transient, 200, code).With(beside),

                    // NOT_FOUND, or a code this was not written against: not something another attempt changes.
                    // The documented code is carried through rather than flattened to a label, because what it
                    // says can be the answer a caller wanted — a removal asking for a post the provider does
                    // not have is told exactly that, and treats it as success.
                    _ => Note(BufferReplyKind.Unusable, 200, code ?? "unknown").With(beside),
                };
            }

            return root.TryGetProperty("data", out var data) && data.ValueKind is JsonValueKind.Object
                ? BufferReply.Of(data.Clone())
                : Note(BufferReplyKind.Unusable, 200, "no_data");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The attempt's own clock, not the caller's: Buffer did not answer in time.
            return Note(BufferReplyKind.Transient, status: 0, code: "timeout");
        }
        catch (HttpRequestException)
        {
            // By type alone. The message can carry the address, and nothing here needs it.
            return Note(BufferReplyKind.Transient, status: 0, code: "connection");
        }
        catch (JsonException)
        {
            return Note(BufferReplyKind.Unusable, status: 200, code: "unreadable_body");
        }
        catch (IOException)
        {
            // The body stopped arriving. By type alone, and classified as transient rather than allowed to
            // escape: a caller that saw an exception here would have no outcome to record for an attempt that
            // may well have been acted on.
            return Note(BufferReplyKind.Transient, status: 0, code: "connection");
        }
    }

    /// <summary>Records the shape of a failed attempt and hands the reply back. No body, id, message or key.</summary>
    private BufferReply Note(BufferReplyKind kind, int status, string? code)
    {
        logger.LogWarning(
            "Buffer request attempt ended {ReplyKind} (HTTP {Status}, code {Code}).", kind, status, code ?? "none");

        return new BufferReply(kind, Status: status, Code: code);
    }

    private static async Task<JsonDocument?> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Bounded: a response is a stranger's bytes, and nothing this API returns is anywhere near this size.
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
}
