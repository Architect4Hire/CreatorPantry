using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.AiProvider;

// MEAI001: see UnconfiguredImageGenerator for why this is suppressed per file rather than project-wide.
#pragma warning disable MEAI001

/// <summary>
/// Venice.ai's image API as the rest of the application is allowed to see it: an
/// <see cref="IImageGenerator"/> whose failures arrive in the one shape the Media module's gateway may name.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The native route, not the OpenAI-compatible one.</strong> Venice serves both.
/// <c>/images/generations</c> would have let an OpenAI SDK client be pointed here, but it caps a prompt at
/// 1,500 characters where a creator may write 4,000 and an avoid list beside it, and it answers an unknown
/// model id with Venice's default model instead of an error — so provenance would record a model that did not
/// make the picture. <c>/image/generate</c> requires the model, takes the longer prompt, and is the only one
/// of the two that can be told not to watermark. It needs no SDK, so this is plain HTTP.
/// </para>
/// <para>
/// <strong>The provider's words are dropped, not forwarded.</strong> An error body can quote the prompt back,
/// and a prompt is private creator content (ai.md). A failure crosses as <see cref="HttpRequestException"/>
/// carrying the status and one of this application's own sentences; the body is never read. That is the
/// shape <c>GeneratedImageProviderGateway</c> already maps: 429 is rate-limited, any other 4xx is refused and
/// terminal, 408/5xx/unreachable is unavailable.
/// </para>
/// <para>
/// <strong>A picture Venice flagged is a refusal, not a result.</strong> With safe mode on, a request it
/// objects to still answers 200 — with a blurred image and a header saying so. Staging that would offer a
/// creator a smear as the thing they asked for, so it is reported the way a refusal is.
/// </para>
/// <para>
/// <strong>No size is sent.</strong> Venice's models disagree about how one is expressed — pixels for some,
/// an aspect ratio or a resolution tier for others, and each rejects the other's fields — and nothing above
/// this asks for one yet. Each model's own default applies.
/// </para>
/// </remarks>
public sealed class VeniceImageGenerator(
    HttpClient http,
    string model,
    ILogger<VeniceImageGenerator> logger) : IImageGenerator
{
    /// <summary>The name provenance records as the provider of every image this generates.</summary>
    public const string ProviderName = "venice";

    /// <summary>
    /// The status a flagged picture is reported under. Venice answered 200, so there is no status of its own
    /// to carry; this one is in the range the gateway reads as "it decided, and would decide the same again".
    /// </summary>
    public const HttpStatusCode FlaggedStatus = HttpStatusCode.UnprocessableEntity;

    private const string GeneratePath = "image/generate";
    private const string ImageMediaType = "image/png";

    private readonly ImageGeneratorMetadata metadata = new(ProviderName, http.BaseAddress, model);

    public async Task<ImageGenerationResponse> GenerateAsync(
        ImageGenerationRequest request,
        ImageGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);

        var clock = Stopwatch.StartNew();

        using var response = await http.PostAsJsonAsync(
            GeneratePath,
            new GenerateRequest(options?.ModelId ?? model, request.Prompt),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Venice refused an image after {ElapsedMs} ms with status {Status}.",
                clock.ElapsedMilliseconds,
                (int)response.StatusCode);

            throw new HttpRequestException(
                $"Venice answered with status {(int)response.StatusCode}.", inner: null, response.StatusCode);
        }

        if (IsFlagged(response, "x-venice-is-content-violation") || IsFlagged(response, "x-venice-is-blurred"))
        {
            logger.LogWarning(
                "Venice flagged an image after {ElapsedMs} ms; it was not staged.", clock.ElapsedMilliseconds);

            throw new HttpRequestException(
                "Venice flagged the image it generated.", inner: null, FlaggedStatus);
        }

        var bytes = await ReadImageAsync(response, cancellationToken);

        logger.LogInformation("Venice answered in {ElapsedMs} ms.", clock.ElapsedMilliseconds);

        // No content when there was no usable image: the gateway reads that as an invalid response, which is
        // what it is, where an exception from here would be retried as an outage and paid for again.
        return bytes is null
            ? new ImageGenerationResponse()
            : new ImageGenerationResponse([new DataContent(bytes, ImageMediaType)]);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        return serviceType == typeof(ImageGeneratorMetadata) ? metadata
            : serviceType.IsInstanceOfType(this) ? this
            : null;
    }

    public void Dispose()
    {
        // The HttpClient belongs to the factory that made it.
    }

    private static bool IsFlagged(HttpResponseMessage response, string header) =>
        response.Headers.TryGetValues(header, out var values)
        && values.Any(value => string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase));

    private async Task<byte[]?> ReadImageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<GenerateResponse>(cancellationToken);

            return body?.Images?.FirstOrDefault() is { Length: > 0 } image
                ? Convert.FromBase64String(image)
                : null;
        }
        catch (Exception reading) when (reading is JsonException or FormatException)
        {
            logger.LogError("Venice answered 200 with a body that was not an image: {ExceptionType}.", reading.GetType().Name);

            return null;
        }
    }

    /// <summary>
    /// The request body. PNG because that is what staging has been checking since the first provider; no
    /// watermark because the picture is the creator's; safe mode stated rather than left to Venice's default,
    /// since the handling of a flagged picture above depends on it.
    /// </summary>
    private sealed record GenerateRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt)
    {
        [JsonPropertyName("format")]
        public string Format => "png";

        [JsonPropertyName("hide_watermark")]
        public bool HideWatermark => true;

        [JsonPropertyName("safe_mode")]
        public bool SafeMode => true;

        [JsonPropertyName("return_binary")]
        public bool ReturnBinary => false;
    }

    private sealed record GenerateResponse([property: JsonPropertyName("images")] string[]? Images);
}
